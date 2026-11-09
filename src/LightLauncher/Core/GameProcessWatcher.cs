using System.Diagnostics;
using LightLauncher.Infrastructure;

namespace LightLauncher.Core;

/// <summary>
/// Vigila el ciclo de vida de un juego combinando hasta tres señales:
/// <list type="bullet">
///   <item><b>PID raíz</b>: el proceso que lanzamos nosotros (ejecutables locales).</item>
///   <item><b>Carpeta</b>: procesos cuyo ejecutable vive dentro de la carpeta del juego
///         (cubre launchers que lanzan el juego real y salen). Se enganchan a <see cref="Process.Exited"/>.</item>
///   <item><b>Registro de Steam</b>: <c>RunningAppID</c> / <c>Apps\{id}\Running</c> para juegos lanzados vía
///         <c>steam://</c>, cuyo PID no conocemos.</item>
/// </list>
/// Tolera el patrón "launcher → juego real" con un margen de gracia antes de dar la sesión por cerrada.
/// </summary>
internal sealed class GameProcessWatcher
{
    private readonly string? _steamAppId;
    private readonly int _steamAppIdNumber;
    private readonly string? _watchDir;
    private readonly TimeSpan _relaunchGrace;
    private int? _rootPid;
    private long _lastAliveTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Tiempo transcurrido desde la última vez que el juego se vio vivo. Tras <see cref="WaitForExitAsync"/>
    /// equivale al margen de gracia esperado, que no debe contarse como tiempo jugado.
    /// </summary>
    public TimeSpan TimeSinceLastAlive => Stopwatch.GetElapsedTime(_lastAliveTimestamp);

    /// <param name="steamAppId">AppID para consultar el Registro de Steam; null para ejecutables locales.</param>
    /// <param name="watchDir">Carpeta cuyos procesos cuentan como "el juego"; null para no vigilar carpeta.</param>
    /// <param name="rootPid">PID del proceso lanzado directamente, si se conoce.</param>
    public GameProcessWatcher(string? steamAppId, string? watchDir, int? rootPid, TimeSpan relaunchGrace)
    {
        _steamAppId = steamAppId;
        if (steamAppId is not null) _ = int.TryParse(steamAppId, out _steamAppIdNumber);
        _watchDir = watchDir is not null && Directory.Exists(watchDir) ? watchDir : null;
        _rootPid = rootPid;
        _relaunchGrace = relaunchGrace;
    }

    /// <summary>Espera a que el juego arranque. Devuelve false si vence el plazo.</summary>
    public Task<bool> WaitForStartAsync(TimeSpan timeout, CancellationToken ct) =>
        ProcessUtils.WaitUntilAsync(IsGameAlive, timeout, ct, pollMs: 1000);

    /// <summary>Completa cuando el juego (y cualquier relanzamiento) ha terminado.</summary>
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pids = CollectPids();
            if (pids.Count > 0)
            {
                // Ruta rápida: listener sobre los procesos reales del juego.
                await WaitForAnyExitAsync(pids, ct).ConfigureAwait(false);
                MarkAlive(); // vivo hasta este instante (o hasta el próximo sondeo)
                continue;
            }

            if (IsFlaggedRunningBySteam())
            {
                MarkAlive();
                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            // Nada vivo: margen de gracia por si el juego se relanza a sí mismo.
            var revived = await ProcessUtils
                .WaitUntilAsync(IsGameAlive, _relaunchGrace, ct, pollMs: 1000)
                .ConfigureAwait(false);
            if (!revived)
            {
                Log.Info($"Juego terminado (steam={_steamAppId ?? "-"}, dir={_watchDir ?? "-"}).");
                return;
            }
        }
    }

    private List<int> CollectPids()
    {
        var pids = _watchDir is null ? [] : ProcessUtils.FindProcessesUnder(_watchDir);
        if (_rootPid is int root)
        {
            if (ProcessUtils.IsAlive(root)) { if (!pids.Contains(root)) pids.Add(root); }
            else _rootPid = null; // muerto: lo olvidamos para no confundirlo con un PID reutilizado
        }
        return pids;
    }

    private bool IsGameAlive()
    {
        var alive = CollectPids().Count > 0 || IsFlaggedRunningBySteam();
        if (alive) MarkAlive();
        return alive;
    }

    private void MarkAlive() => _lastAliveTimestamp = Stopwatch.GetTimestamp();

    private bool IsFlaggedRunningBySteam()
    {
        if (_steamAppId is null) return false;
        // Si Steam murió, el registro puede quedar obsoleto: solo nos fiamos con el cliente vivo.
        if (!SteamLocator.IsClientRunning()) return false;
        return (_steamAppIdNumber != 0 && SteamLocator.GetRunningAppId() == _steamAppIdNumber)
               || SteamLocator.IsAppFlaggedRunning(_steamAppId);
    }

    /// <summary>
    /// Se engancha a <see cref="Process.Exited"/> de cada PID y despierta en cuanto uno termina.
    /// Si no hay permiso para el evento (juego elevado / anti-cheat), cae a sondeo periódico.
    /// </summary>
    private static async Task WaitForAnyExitAsync(List<int> pids, CancellationToken ct)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = new List<Process>();
        bool allHooked = true;

        try
        {
            foreach (var pid in pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    tracked.Add(p);
                    p.EnableRaisingEvents = true;
                    p.Exited += (_, _) => exited.TrySetResult();
                    if (p.HasExited) exited.TrySetResult();
                }
                catch (Exception)
                {
                    allHooked = false; // sin acceso o ya terminó
                }
            }

            // Con eventos fiables basta un sondeo largo (para detectar procesos hijos nuevos).
            var poll = TimeSpan.FromSeconds(allHooked ? 15 : 3);
            await Task.WhenAny(exited.Task, Task.Delay(poll, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            foreach (var p in tracked) p.Dispose();
        }
    }

    /// <summary>
    /// ¿Es razonable tratar todos los procesos de esta carpeta como "el juego"? Se descarta para
    /// raíces de unidad y carpetas compartidas (Windows, Program Files, Escritorio, Descargas…),
    /// donde se colarían programas ajenos.
    /// </summary>
    public static bool IsSafeWatchDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception) { return false; }

        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return false;

        Environment.SpecialFolder[] shared =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
        ];
        var blocked = shared.Select(Environment.GetFolderPath)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar));

        if (blocked.Any(b => string.Equals(b, full, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Nada dentro de C:\Windows.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length == 0 || !full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
