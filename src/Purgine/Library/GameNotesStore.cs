using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>Notas y reseña personal de un juego.</summary>
public sealed class GameNotes
{
    public string Notes { get; set; } = string.Empty;
    public string Review { get; set; } = string.Empty;
    /// <summary>true = recomendado, false = no recomendado, null = sin decidir.</summary>
    public bool? Recommended { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Notes.Length == 0 && Review.Length == 0 && Recommended is null;
}

/// <summary>
/// Notas y reseñas personales por juego en <c>game-notes.json</c> (clave = id del juego: «steam:730», «local:…»).
/// Solo locales: nunca se envían a ningún sitio. Escritura atómica y diferida (no se escribe en cada tecla).
/// </summary>
public sealed class GameNotesStore : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, GameNotes> _data;
    private readonly DebouncedAction _save;

    public GameNotesStore(string path)
    {
        _path = path;
        _data = JsonFileStore.Load<Dictionary<string, GameNotes>>(path);
        _save = new DebouncedAction(Persist, TimeSpan.FromMilliseconds(700));
    }

    /// <summary>Copia de las notas del juego (vacías si no tiene).</summary>
    public GameNotes Get(string gameId)
    {
        lock (_gate)
        {
            return _data.TryGetValue(gameId, out var n)
                ? new GameNotes { Notes = n.Notes, Review = n.Review, Recommended = n.Recommended, UpdatedUtc = n.UpdatedUtc }
                : new GameNotes();
        }
    }

    /// <summary>Modifica las notas del juego y programa el guardado.</summary>
    public void Update(string gameId, Action<GameNotes> change)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(gameId, out var n)) n = _data[gameId] = new GameNotes();
            change(n);
            n.UpdatedUtc = DateTimeOffset.UtcNow;
            if (n.IsEmpty) _data.Remove(gameId);
        }
        _save.Request();
    }

    /// <summary>Guarda ya lo pendiente (al cerrar la ficha o la aplicación).</summary>
    public void Flush() => _save.Flush();

    private void Persist()
    {
        Dictionary<string, GameNotes> copy;
        lock (_gate) copy = new Dictionary<string, GameNotes>(_data);
        JsonFileStore.Save(_path, copy);
    }

    public void Dispose()
    {
        _save.Flush();
        _save.Dispose();
    }
}
