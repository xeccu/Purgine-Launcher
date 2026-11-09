using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>
/// Vigila las carpetas <c>steamapps</c> con <see cref="FileSystemWatcher"/> para reflejar en vivo,
/// sin re-escanear todo, las instalaciones nuevas, el progreso de las descargas y las desinstalaciones
/// que se hagan desde Steam. Los eventos se agrupan (debounce) porque Steam reescribe el manifiesto
/// varias veces por segundo durante una descarga.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _pendingChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeleted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Timer _timer;

    /// <summary>Un manifiesto se creó o cambió (ruta completa).</summary>
    public event Action<string>? ManifestChanged;

    /// <summary>Un manifiesto desapareció (AppID): el juego se desinstaló.</summary>
    public event Action<string>? ManifestDeleted;

    public LibraryWatcher() => _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Sustituye las carpetas vigiladas por las raíces de biblioteca indicadas.</summary>
    public void Watch(IEnumerable<string> libraryFolders)
    {
        StopWatchers();
        foreach (var library in libraryFolders)
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            try
            {
                var w = new FileSystemWatcher(steamapps, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 8192, // mínimo: pocos archivos, poca memoria
                };
                w.Created += (_, e) => Queue(e.FullPath, deleted: false);
                w.Changed += (_, e) => Queue(e.FullPath, deleted: false);
                w.Renamed += (_, e) => Queue(e.FullPath, deleted: false);
                w.Deleted += (_, e) => Queue(e.FullPath, deleted: true);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Warn($"No se puede vigilar {steamapps}: {ex.Message}");
            }
        }
    }

    private void Queue(string path, bool deleted)
    {
        lock (_gate)
        {
            if (deleted) { _pendingDeleted.Add(path); _pendingChanged.Remove(path); }
            else { _pendingChanged.Add(path); _pendingDeleted.Remove(path); }
        }
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        string[] changed, deleted;
        lock (_gate)
        {
            changed = _pendingChanged.ToArray();
            deleted = _pendingDeleted.ToArray();
            _pendingChanged.Clear();
            _pendingDeleted.Clear();
        }

        foreach (var path in changed)
        {
            if (File.Exists(path)) ManifestChanged?.Invoke(path);
        }
        foreach (var path in deleted)
        {
            var appId = SteamLibraryScanner.AppIdFromManifestPath(path);
            if (appId is not null && !File.Exists(path)) ManifestDeleted?.Invoke(appId);
        }
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    public void Dispose()
    {
        StopWatchers();
        _timer.Dispose();
    }
}
