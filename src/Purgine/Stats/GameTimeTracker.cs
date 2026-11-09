using System.Diagnostics;
using Purgine.Infrastructure;
using Purgine.Library;

namespace Purgine.Stats;

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

/// <summary>Desglose del tiempo jugado de un juego.</summary>
/// <param name="Steam">Histórico que guarda Steam (localconfig.vdf).</param>
/// <param name="LocalExtra">Sesiones de Purgine que Steam todavía no refleja (o juegos locales).</param>
public readonly record struct PlaytimeBreakdown(TimeSpan Steam, TimeSpan LocalExtra)
{
    public TimeSpan Total => Steam + LocalExtra;
}

/// <summary>
/// Tiempo jugado: histórico de Steam + sesiones medidas por Purgine.
/// <para><b>Sin doble conteo:</b> los juegos de Steam lanzados desde aquí se siguen ejecutando a través
/// de Steam, que también suma ese tiempo. Por eso a cada juego de Steam solo se le añaden las sesiones
/// locales posteriores a su <c>LastPlayed</c> (las que Steam aún no ha volcado a disco).
/// Los ejecutables locales usan solo el tiempo propio.</para>
/// <para>La duración se mide con <see cref="Stopwatch"/> (monótono): los cambios de hora no la falsean.
/// Un latido por minuto permite recuperar la sesión si el launcher muere a mitad de partida.</para>
/// </summary>
public sealed class GameTimeTracker
{
    /// <summary>
    /// Margen para decidir si Steam ya contó una sesión: Steam anota <c>LastPlayed</c> al lanzar el
    /// juego, y nuestra sesión empieza unos segundos/minutos después (al detectar el proceso).
    /// </summary>
    private static readonly TimeSpan SteamRecordedTolerance = TimeSpan.FromMinutes(5);

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly PlaytimeData _data;
    private readonly Dictionary<string, List<PlaySession>> _byGame = new();
    private Dictionary<string, SteamAppUsage> _steam = new(); // clave: gameId (steam:{appid})
    private Stopwatch? _stopwatch;

    public event EventHandler<PlaySession>? SessionRecorded;

    public GameTimeTracker(string filePath)
    {
        _filePath = filePath;
        _data = JsonFileStore.Load<PlaytimeData>(filePath);
        RecoverOrphanSession();
        foreach (var s in _data.Sessions) Index(s);
    }

    public bool HasActiveSession { get { lock (_gate) return _data.Active is not null; } }

    /// <summary>Sustituye el histórico de Steam (se llama al escanear y tras cada sesión de Steam).</summary>
    public void SetSteamPlaytime(IReadOnlyDictionary<string, SteamAppUsage> byAppId)
    {
        var map = byAppId.Values.ToDictionary(u => GameEntry.MakeSteamId(u.AppId));
        lock (_gate) _steam = map;
    }

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
    /// <param name="excludeTail">
    /// Tiempo final que no cuenta como jugado (p. ej. el margen de gracia esperando un relanzamiento).
    /// </param>
    public PlaySession? EndSession(TimeSpan excludeTail = default)
    {
        PlaySession session;
        lock (_gate)
        {
            var active = _data.Active;
            if (active is null) return null;

            var duration = _stopwatch?.Elapsed ?? DateTimeOffset.UtcNow - active.StartUtc;
            if (excludeTail > TimeSpan.Zero)
                duration = duration > excludeTail ? duration - excludeTail : TimeSpan.Zero;
            _stopwatch = null;
            session = new PlaySession(
                active.GameId, active.GameName, active.StartUtc,
                active.StartUtc + duration, Math.Round(duration.TotalSeconds, 1));

            _data.Active = null;
            _data.Sessions.Add(session);
            Index(session);
            Persist();
        }
        Log.Info($"Sesión cerrada: {session.GameName}, {TimeSpan.FromSeconds(session.DurationSeconds):hh\\:mm\\:ss}.");
        SessionRecorded?.Invoke(this, session);
        return session;
    }

    /// <summary>Tiempo total mostrado en la UI: Steam + sesiones locales que Steam aún no refleja.</summary>
    public TimeSpan GetTotal(string gameId) => GetBreakdown(gameId).Total;

    public PlaytimeBreakdown GetBreakdown(string gameId)
    {
        lock (_gate) return BreakdownUnlocked(gameId);
    }

    public TimeSpan GetGrandTotal()
    {
        lock (_gate)
        {
            var ids = _byGame.Keys.Union(_steam.Keys);
            return ids.Aggregate(TimeSpan.Zero, (acc, id) => acc + BreakdownUnlocked(id).Total);
        }
    }

    public DateTimeOffset? GetLastPlayed(string gameId)
    {
        lock (_gate)
        {
            DateTimeOffset? local = _byGame.TryGetValue(gameId, out var list) && list.Count > 0
                ? list.Max(s => s.EndUtc)
                : null;
            var steam = _steam.TryGetValue(gameId, out var u) ? u.LastPlayed : null;
            if (local is null) return steam;
            if (steam is null) return local;
            return local > steam ? local : steam;
        }
    }

    public IReadOnlyList<PlaySession> GetSessions(string gameId)
    {
        lock (_gate) return _byGame.TryGetValue(gameId, out var list) ? list.ToList() : [];
    }

    private PlaytimeBreakdown BreakdownUnlocked(string gameId)
    {
        var sessions = _byGame.TryGetValue(gameId, out var list) ? list : [];
        if (!_steam.TryGetValue(gameId, out var steam))
            return new PlaytimeBreakdown(TimeSpan.Zero, Sum(sessions));

        // Solo lo que Steam todavía no ha registrado (sesiones posteriores a su LastPlayed).
        var cutoff = steam.LastPlayed is { } lp ? lp + SteamRecordedTolerance : DateTimeOffset.MinValue;
        var extra = Sum(sessions.Where(s => s.StartUtc > cutoff));
        return new PlaytimeBreakdown(steam.Playtime, extra);
    }

    private static TimeSpan Sum(IEnumerable<PlaySession> sessions) =>
        TimeSpan.FromSeconds(sessions.Sum(s => s.DurationSeconds));

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

    private void Index(PlaySession s)
    {
        if (!_byGame.TryGetValue(s.GameId, out var list)) _byGame[s.GameId] = list = [];
        list.Add(s);
    }

    private void Persist() => JsonFileStore.Save(_filePath, _data);
}
