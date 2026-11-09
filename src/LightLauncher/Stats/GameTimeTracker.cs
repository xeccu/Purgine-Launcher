using System.Diagnostics;
using LightLauncher.Infrastructure;

namespace LightLauncher.Stats;

/// <summary>Una sesión de juego cerrada.</summary>
/// <param name="Recovered">True si se reconstruyó tras un cierre inesperado del launcher (duración aproximada).</param>
public sealed record PlaySession(
    string GameId,
    string GameName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DurationSeconds,
    bool Recovered = false);

/// <summary>Sesión en curso, persistida para poder recuperarla si el launcher se cierra de golpe.</summary>
public sealed class ActiveSession
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
}

public sealed class PlaytimeData
{
    public List<PlaySession> Sessions { get; set; } = [];
    public ActiveSession? Active { get; set; }
}

/// <summary>
/// Registro local del tiempo jugado. La duración se mide con <see cref="Stopwatch"/> (monótono),
/// así que los cambios de hora del sistema no la falsean. Cada minuto se guarda un "latido"
/// para que, si el launcher muere a mitad de partida, la sesión se recupere al reiniciar.
/// </summary>
public sealed class GameTimeTracker
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly PlaytimeData _data;
    private readonly Dictionary<string, double> _totals = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPlayed = new();
    private Stopwatch? _stopwatch;

    public event EventHandler<PlaySession>? SessionRecorded;

    public GameTimeTracker(string filePath)
    {
        _filePath = filePath;
        _data = JsonFileStore.Load<PlaytimeData>(filePath);
        RecoverOrphanSession();
        foreach (var s in _data.Sessions) Accumulate(s);
    }

    public bool HasActiveSession { get { lock (_gate) return _data.Active is not null; } }

    public void StartSession(string gameId, string gameName)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _data.Active = new ActiveSession
            {
                GameId = gameId,
                GameName = gameName,
                StartUtc = now,
                LastHeartbeatUtc = now,
            };
            _stopwatch = Stopwatch.StartNew();
            Persist();
        }
        Log.Info($"Sesión iniciada: {gameName} ({gameId}).");
    }

    /// <summary>Guarda el instante actual como prueba de vida de la sesión en curso.</summary>
    public void Heartbeat()
    {
        lock (_gate)
        {
            if (_data.Active is null) return;
            _data.Active.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    /// <summary>Cierra la sesión activa (si hay) y la añade al histórico.</summary>
    public PlaySession? EndSession()
    {
        PlaySession session;
        lock (_gate)
        {
            var active = _data.Active;
            if (active is null) return null;

            var duration = _stopwatch?.Elapsed ?? DateTimeOffset.UtcNow - active.StartUtc;
            _stopwatch = null;
            session = new PlaySession(
                active.GameId, active.GameName, active.StartUtc,
                active.StartUtc + duration, Math.Round(duration.TotalSeconds, 1));

            _data.Active = null;
            _data.Sessions.Add(session);
            Accumulate(session);
            Persist();
        }
        Log.Info($"Sesión cerrada: {session.GameName}, {TimeSpan.FromSeconds(session.DurationSeconds):hh\\:mm\\:ss}.");
        SessionRecorded?.Invoke(this, session);
        return session;
    }

    public TimeSpan GetTotal(string gameId)
    {
        lock (_gate) return TimeSpan.FromSeconds(_totals.GetValueOrDefault(gameId));
    }

    public TimeSpan GetGrandTotal()
    {
        lock (_gate) return TimeSpan.FromSeconds(_totals.Values.Sum());
    }

    public DateTimeOffset? GetLastPlayed(string gameId)
    {
        lock (_gate) return _lastPlayed.TryGetValue(gameId, out var t) ? t : null;
    }

    public IReadOnlyList<PlaySession> GetSessions(string gameId)
    {
        lock (_gate) return _data.Sessions.Where(s => s.GameId == gameId).ToList();
    }

    private void RecoverOrphanSession()
    {
        var orphan = _data.Active;
        if (orphan is null) return;

        var duration = orphan.LastHeartbeatUtc - orphan.StartUtc;
        if (duration > TimeSpan.Zero)
        {
            _data.Sessions.Add(new PlaySession(
                orphan.GameId, orphan.GameName, orphan.StartUtc, orphan.LastHeartbeatUtc,
                Math.Round(duration.TotalSeconds, 1), Recovered: true));
            Log.Warn($"Sesión huérfana recuperada: {orphan.GameName} ({duration:hh\\:mm\\:ss}, aprox.).");
        }
        _data.Active = null;
        Persist();
    }

    private void Accumulate(PlaySession s)
    {
        _totals[s.GameId] = _totals.GetValueOrDefault(s.GameId) + s.DurationSeconds;
        if (!_lastPlayed.TryGetValue(s.GameId, out var last) || s.EndUtc > last)
            _lastPlayed[s.GameId] = s.EndUtc;
    }

    private void Persist() => JsonFileStore.Save(_filePath, _data);
}
