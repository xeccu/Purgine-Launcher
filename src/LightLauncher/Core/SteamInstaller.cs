using System.Text;
using LightLauncher.Infrastructure;
using LightLauncher.Library;

namespace LightLauncher.Core;

public enum InstallStart
{
    /// <summary>Manifiesto creado y Steam arrancado en segundo plano: la descarga empieza sola.</summary>
    Started,
    /// <summary>Manifiesto creado y Steam reiniciado en segundo plano.</summary>
    StartedAfterSteamRestart,
    /// <summary>Manifiesto creado; Steam está abierto y no se reinició: empezará al reabrir Steam.</summary>
    QueuedUntilSteamRestart,
    /// <summary>Ya había un manifiesto (instalado o instalándose).</summary>
    AlreadyPresent,
    Failed,
}

public sealed record InstallOutcome(InstallStart Result, string Message, bool SteamStartedByUs = false);

/// <summary>
/// Instalación "fantasma" de juegos de Steam, sin pasar por la ventana de Steam.
/// <para><b>Cómo:</b> se crea en la biblioteca con más espacio libre un manifiesto
/// <c>appmanifest_{appid}.acf</c> con <c>StateFlags = 1026</c> (actualización requerida). Al arrancar,
/// Steam lo trata como un juego pendiente de descarga y lo baja en segundo plano. Steam solo lee los
/// manifiestos nuevos al arrancar: si ya está abierto, hay que reiniciarlo (siempre con permiso).</para>
/// <para><b>Aviso:</b> es un mecanismo no documentado de Steam. Si en algún momento deja de funcionar,
/// la alternativa es <c>steam://install/{appid}</c> (diálogo oficial).</para>
/// </summary>
public sealed class SteamInstaller
{
    /// <summary>UpdateRequired (2) | UpdateStarted (1024).</summary>
    private const int StateFlagsPendingDownload = 1026;

    private readonly SteamLocator _steam;
    private readonly SteamLibraryScanner _scanner;
    private readonly ProcessOrchestrator _orchestrator;

    public SteamInstaller(SteamLocator steam, SteamLibraryScanner scanner, ProcessOrchestrator orchestrator)
    {
        _steam = steam;
        _scanner = scanner;
        _orchestrator = orchestrator;
    }

    /// <summary>¿Habrá que reiniciar Steam para que empiece la descarga?</summary>
    public static bool RequiresSteamRestart => SteamLocator.IsClientRunning();

    /// <summary>
    /// Inicia la instalación en segundo plano.
    /// </summary>
    /// <param name="restartSteamIfRunning">Si Steam está abierto, reiniciarlo (minimizado) para que empiece ya.</param>
    public async Task<InstallOutcome> InstallAsync(GameEntry game, bool restartSteamIfRunning, CancellationToken ct = default)
    {
        if (_steam.SteamExe is null)
            return new InstallOutcome(InstallStart.Failed, "No se encontró Steam en este equipo.");
        if (_scanner.FindGame(game.AppId) is not null)
            return new InstallOutcome(InstallStart.AlreadyPresent, $"{game.Name} ya está instalado o en cola.");

        string manifest;
        try
        {
            manifest = WriteManifest(game);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo crear el manifiesto de {game.Name}", ex);
            return new InstallOutcome(InstallStart.Failed, $"No se pudo preparar la instalación: {ex.Message}");
        }
        Log.Info($"Instalación en segundo plano preparada: {manifest}");

        if (SteamLocator.IsClientRunning())
        {
            if (!restartSteamIfRunning)
                return new InstallOutcome(InstallStart.QueuedUntilSteamRestart,
                    $"{game.Name} está en cola: la descarga empezará la próxima vez que se abra Steam.");

            if (!await _orchestrator.RestartSteamInBackgroundAsync(ct).ConfigureAwait(false))
                return new InstallOutcome(InstallStart.QueuedUntilSteamRestart,
                    $"{game.Name} está en cola. Steam está ocupado (partida o descargas en curso): se reiniciará en segundo plano cuando termine para empezarla.");

            return new InstallOutcome(InstallStart.StartedAfterSteamRestart,
                $"Descargando {game.Name} en segundo plano…", SteamStartedByUs: true);
        }

        await _orchestrator.StartSteamInBackgroundAsync(ct).ConfigureAwait(false);
        return new InstallOutcome(InstallStart.Started, $"Descargando {game.Name} en segundo plano…", SteamStartedByUs: true);
    }

    /// <summary>Crea el manifiesto de "pendiente de descarga" y devuelve su ruta.</summary>
    public string WriteManifest(GameEntry game)
    {
        var library = ChooseLibrary(_scanner.GetLibraryFolders())
                      ?? throw new InvalidOperationException("No hay ninguna biblioteca de Steam.");
        var steamapps = Path.Combine(library, "steamapps");
        var path = Path.Combine(steamapps, $"appmanifest_{game.AppId}.acf");
        if (File.Exists(path)) return path;

        var text = BuildManifestText(game, SteamPlaytimeReader.CurrentSteamId64(_steam.SteamPath));

        // Escritura atómica: Steam nunca debe ver un manifiesto a medias.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: false);
        return path;
    }

    /// <summary>Contenido KeyValues de un manifiesto "pendiente de descarga".</summary>
    public static string BuildManifestText(GameEntry game, ulong? owner)
    {
        var installDir = string.IsNullOrWhiteSpace(game.InstallDirName) ? SanitizeFolderName(game.Name) : game.InstallDirName;
        var sb = new StringBuilder();
        sb.Append("\"AppState\"\n{\n");
        void Kv(string k, string v) => sb.Append("\t\"").Append(k).Append("\"\t\t\"").Append(Escape(v)).Append("\"\n");
        Kv("appid", game.AppId);
        Kv("Universe", "1");
        Kv("name", game.Name);
        Kv("StateFlags", StateFlagsPendingDownload.ToString());
        Kv("installdir", installDir);
        Kv("LastUpdated", "0");
        Kv("SizeOnDisk", "0");
        Kv("buildid", "0");
        if (owner is not null) Kv("LastOwner", owner.Value.ToString());
        Kv("BytesToDownload", "0");
        Kv("BytesDownloaded", "0");
        Kv("AutoUpdateBehavior", "0");
        Kv("AllowOtherDownloadsWhileRunning", "0");
        Kv("ScheduledAutoUpdate", "0");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Elimina un manifiesto pendiente que aún no ha empezado a descargar (deshacer).</summary>
    public bool CancelPending(string appId)
    {
        var game = _scanner.FindGame(appId);
        if (game is null || game.IsFullyInstalled is true || game.BytesDownloaded > 0 || game.SizeOnDisk > 0) return false;
        foreach (var library in _scanner.GetLibraryFolders())
        {
            var path = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            try
            {
                if (File.Exists(path)) { File.Delete(path); return true; }
            }
            catch (Exception ex) { Log.Warn($"No se pudo cancelar {path}: {ex.Message}"); }
        }
        return false;
    }

    /// <summary>La biblioteca con más espacio libre.</summary>
    private static string? ChooseLibrary(IReadOnlyList<string> libraries) =>
        libraries
            .Select(l => (Path: l, Free: FreeSpace(l)))
            .OrderByDescending(x => x.Free)
            .Select(x => x.Path)
            .FirstOrDefault();

    private static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != ':' ).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Game" : clean;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
