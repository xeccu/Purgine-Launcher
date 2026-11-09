using LightLauncher.Infrastructure;

namespace LightLauncher.Library;

/// <summary>Ejecutable local añadido por el usuario.</summary>
public sealed class LocalGameDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>Copia de la portada dentro de la carpeta de datos (sobrevive aunque se borre el original).</summary>
    public string? CoverImagePath { get; set; }
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    public GameEntry ToEntry() => new()
    {
        Id = GameEntry.MakeLocalId(Id),
        Platform = GamePlatform.Local,
        AppId = Id,
        Name = Name,
        ExecutablePath = ExecutablePath,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        InstallDir = Path.GetDirectoryName(ExecutablePath),
        CoverImagePath = CoverImagePath is not null && File.Exists(CoverImagePath) ? CoverImagePath : null,
    };
}

/// <summary>Contenido de <c>library.json</c>.</summary>
public sealed class UserLibraryData
{
    public List<string> Tags { get; set; } = ["Favoritos", "En progreso", "Completados", "Proyectos"];
    /// <summary>gameId → etiquetas asignadas.</summary>
    public Dictionary<string, List<string>> GameTags { get; set; } = new();
    public List<LocalGameDefinition> LocalGames { get; set; } = [];
}

/// <summary>
/// Organización personal de la biblioteca: etiquetas y ejecutables locales.
/// Todo se guarda en <c>library.json</c> dentro de la carpeta de datos.
/// </summary>
public sealed class UserLibrary
{
    private readonly string _filePath;
    private readonly string _coversDir;
    private readonly UserLibraryData _data;
    private readonly object _gate = new();

    /// <summary>Se dispara tras cualquier cambio (etiquetas, asignaciones o juegos locales).</summary>
    public event EventHandler? Changed;

    public UserLibrary(string filePath, string coversDir)
    {
        _filePath = filePath;
        _coversDir = coversDir;
        _data = JsonFileStore.Load<UserLibraryData>(filePath);
    }

    // ===================== Etiquetas =====================

    public IReadOnlyList<string> Tags { get { lock (_gate) return _data.Tags.ToList(); } }

    public bool AddTag(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        lock (_gate)
        {
            if (_data.Tags.Contains(name, StringComparer.CurrentCultureIgnoreCase)) return false;
            _data.Tags.Add(name);
        }
        SaveAndNotify();
        return true;
    }

    public void RemoveTag(string name)
    {
        lock (_gate)
        {
            _data.Tags.RemoveAll(t => t == name);
            foreach (var list in _data.GameTags.Values) list.RemoveAll(t => t == name);
            PruneEmptyAssignments();
        }
        SaveAndNotify();
    }

    public IReadOnlyList<string> GetTags(string gameId)
    {
        lock (_gate) return _data.GameTags.TryGetValue(gameId, out var list) ? list.ToList() : [];
    }

    public bool HasTag(string gameId, string tag)
    {
        lock (_gate) return _data.GameTags.TryGetValue(gameId, out var list) && list.Contains(tag);
    }

    public int CountTagged(string tag)
    {
        lock (_gate) return _data.GameTags.Values.Count(l => l.Contains(tag));
    }

    public void SetTag(string gameId, string tag, bool assigned)
    {
        lock (_gate)
        {
            if (!_data.GameTags.TryGetValue(gameId, out var list))
                _data.GameTags[gameId] = list = [];
            if (assigned && !list.Contains(tag)) list.Add(tag);
            else if (!assigned) list.Remove(tag);
            PruneEmptyAssignments();
        }
        SaveAndNotify();
    }

    // ===================== Ejecutables locales =====================

    public IReadOnlyList<LocalGameDefinition> LocalGames { get { lock (_gate) return _data.LocalGames.ToList(); } }

    public LocalGameDefinition? FindLocal(string id)
    {
        lock (_gate) return _data.LocalGames.FirstOrDefault(g => g.Id == id);
    }

    public void AddOrUpdateLocalGame(LocalGameDefinition game)
    {
        lock (_gate)
        {
            var index = _data.LocalGames.FindIndex(g => g.Id == game.Id);
            if (index >= 0) _data.LocalGames[index] = game;
            else _data.LocalGames.Add(game);
        }
        SaveAndNotify();
    }

    public void RemoveLocalGame(string id)
    {
        LocalGameDefinition? removed;
        lock (_gate)
        {
            removed = _data.LocalGames.FirstOrDefault(g => g.Id == id);
            if (removed is null) return;
            _data.LocalGames.Remove(removed);
            _data.GameTags.Remove(GameEntry.MakeLocalId(id));
        }
        DeleteCoverIfOwned(removed.CoverImagePath);
        SaveAndNotify();
    }

    /// <summary>
    /// Copia una imagen elegida por el usuario a <c>covers/</c> y devuelve la nueva ruta.
    /// Si ya está dentro de esa carpeta, la devuelve tal cual.
    /// </summary>
    public string? ImportCover(string sourcePath, string localGameId)
    {
        try
        {
            var full = Path.GetFullPath(sourcePath);
            if (full.StartsWith(Path.GetFullPath(_coversDir), StringComparison.OrdinalIgnoreCase))
                return full;

            Directory.CreateDirectory(_coversDir);
            var target = Path.Combine(_coversDir, $"{localGameId}-{DateTime.UtcNow.Ticks}{Path.GetExtension(full)}");
            File.Copy(full, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo importar la portada {sourcePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Borra una portada antigua si es una copia nuestra (no toca archivos del usuario).</summary>
    public void DeleteCoverIfOwned(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(_coversDir), StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
        catch (Exception) { /* en uso o ya borrada */ }
    }

    private void PruneEmptyAssignments()
    {
        foreach (var key in _data.GameTags.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            _data.GameTags.Remove(key);
    }

    private void SaveAndNotify()
    {
        lock (_gate) JsonFileStore.Save(_filePath, _data);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
