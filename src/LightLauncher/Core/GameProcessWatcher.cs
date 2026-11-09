using System.Diagnostics;
using LightLauncher.Infrastructure;

namespace LightLauncher.Core;

/// <summary>
/// Vigila el ciclo de vida del juego lanzado vía <c>steam://</c>. Como el protocolo no devuelve el PID,
/// se combinan dos señales:
/// <list type="bullet">
///   <item>Procesos cuyo ejecutable está dentro de la carpeta de instalación del juego
///         (se enganchan al evento <see cref="Process.Exited"/>).</item>
///   <item>El Registro de Steam (<c>RunningAppID</c> / <c>Apps\{id}\Running</c>), que cubre juegos
///         cuyo .exe vive fuera de su carpeta o que no podemos inspeccionar.</item>
/// </list>
/// Tolera el patrón "launcher → juego real" con un margen de gracia antes de dar la sesión por cerrada.
/// </summary>
internal sealed class GameProcessWatcher
{
    private readonly string _appId;
    private readonly int _appIdNumber;
    private readonly string? _installDir;
    private readonly TimeSpan _relaunchGrace;

    public GameProcessWatcher(string appId, string? installDir, TimeSpan relaunchGrace)
    {
        _appId = appId;
        _ = int.TryParse(appId, out _appIdNumber);
        _installDir = installDir is not null && Directory.Exists(installDir) ? installDir : null;
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

            var pids = _installDir is null ? [] : ProcessUtils.FindProcessesUnder(_installDir);
            if (pids.Count > 0)
            {
                // Ruta rápida: listener sobre los procesos reales del juego.
                await WaitForAnyExitAsync(pids, ct).ConfigureAwait(false);
                continue;
            }

            if (IsFlaggedRunningBySteam())
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            // Nada vivo: margen de gracia por si el juego se relanza a sí mismo.
            var revived = await ProcessUtils
                .WaitUntilAsync(IsGameAlive, _relaunchGrace, ct, pollMs: 1000)
                .ConfigureAwait(false);
            if (!revived)
            {
                Log.Info($"Juego {_appId} terminado.");
                return;
            }
        }
    }

    private bool IsGameAlive() =>
        (_installDir is not null && ProcessUtils.FindProcessesUnder(_installDir).Count > 0)
        || IsFlaggedRunningBySteam();

    private bool IsFlaggedRunningBySteam()
    {
        // Si Steam murió, el registro puede quedar obsoleto: solo nos fiamos con el cliente vivo.
        if (!SteamLocator.IsClientRunning()) return false;
        return (_appIdNumber != 0 && SteamLocator.GetRunningAppId() == _appIdNumber)
               || SteamLocator.IsAppFlaggedRunning(_appId);
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
}
