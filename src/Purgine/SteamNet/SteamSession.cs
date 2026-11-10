using Purgine.Infrastructure;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace Purgine.SteamNet;

public enum SteamSessionState
{
    Disconnected,
    Connecting,
    LoggingIn,
    LoggedIn,
    Failed,
}

/// <summary>
/// Conexión directa con los servidores CM de Steam mediante SteamKit2, sin el cliente de escritorio.
/// <list type="bullet">
///   <item>Inicio de sesión con usuario/contraseña + Steam Guard (app móvil o código), o con el refresh token
///         guardado (cifrado). También sesión anónima (contenido público).</item>
///   <item>Licencias de la cuenta, PICS (información de apps/paquetes), claves de depot, códigos de manifiesto
///         y servidores CDN.</item>
/// </list>
/// La conexión se abre bajo demanda y se cierra tras unos minutos sin uso (y sin descargas en curso),
/// para que Purgine siga ocupando el mínimo de memoria.
/// </summary>
public sealed class SteamSession : IAsyncDisposable
{
    private static readonly TimeSpan IdleDisconnect = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    /// <summary>
    /// LoginID propio: Steam distingue sesiones por este valor, así que la de Purgine convive con la del
    /// cliente de escritorio sin expulsarla.
    /// </summary>
    private const uint LauncherLoginId = 0x4C4C_4C31; // "LLL1"

    private readonly SteamCredentialStore _store;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Timer _idleTimer;

    private SteamClient? _client;
    private CallbackManager? _callbacks;
    private SteamUser? _user;
    private SteamApps? _apps;
    private SteamContent? _content;
    private CancellationTokenSource? _pumpCts;
    private TaskCompletionSource<bool>? _connected;
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? _loggedOn;
    private TaskCompletionSource<IReadOnlyList<SteamApps.LicenseListCallback.License>> _licenses = NewLicenseTcs();
    private int _holds;
    private SteamSessionState _state = SteamSessionState.Disconnected;

    public event Action<SteamSessionState, string>? StateChanged;

    public SteamSession(SteamCredentialStore store)
    {
        _store = store;
        _idleTimer = new Timer(_ => _ = DisconnectIfIdleAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public SteamSessionState State { get { lock (_gate) return _state; } }
    public bool IsLoggedIn => State == SteamSessionState.LoggedIn;
    public bool IsAnonymous { get; private set; }
    public string? AccountName { get; private set; }
    public SteamID? SteamId { get; private set; }
    public uint CellId { get; private set; }

    /// <summary>¿Hay una sesión recordada con la que conectar sin pedir contraseña?</summary>
    public bool HasSavedSession => _store.HasSession;

    public string? SavedAccountName => _store.Load()?.AccountName;

    // =====================================================================================
    // Inicio de sesión
    // =====================================================================================

    /// <summary>
    /// Inicia sesión con usuario y contraseña. Steam Guard se resuelve con <paramref name="authenticator"/>
    /// (confirmación en la app móvil o código). Si sale bien, se guarda el refresh token (cifrado), nunca la contraseña.
    /// </summary>
    public async Task LoginWithCredentialsAsync(string username, string password, IAuthenticator authenticator, CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        SetState(SteamSessionState.LoggingIn, "Iniciando sesión en Steam…");

        var previous = _store.Load();
        var auth = await _client!.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = true,
            Authenticator = authenticator,
            DeviceFriendlyName = $"Purgine ({Environment.MachineName})",
            GuardData = string.Equals(previous?.AccountName, username, StringComparison.OrdinalIgnoreCase) ? previous?.GuardData : null,
        }).ConfigureAwait(false);

        var poll = await auth.PollingWaitForResultAsync(ct).ConfigureAwait(false);
        _store.Save(new SavedSteamSession
        {
            AccountName = poll.AccountName,
            RefreshToken = poll.RefreshToken,
            GuardData = poll.NewGuardData ?? previous?.GuardData,
            SteamId64 = auth.SteamID.ConvertToUInt64(),
        });

        await LogOnWithTokenAsync(poll.AccountName, poll.RefreshToken, ct).ConfigureAwait(false);
    }

    /// <summary>Conecta con la sesión guardada. Devuelve false si no hay o Steam la rechaza (caducada/revocada).</summary>
    public async Task<bool> LoginWithSavedSessionAsync(CancellationToken ct)
    {
        if (IsLoggedIn && !IsAnonymous) return true;
        var saved = _store.Load();
        if (saved is null || string.IsNullOrEmpty(saved.RefreshToken)) return false;

        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            SetState(SteamSessionState.LoggingIn, "Conectando con tu cuenta de Steam…");
            await LogOnWithTokenAsync(saved.AccountName, saved.RefreshToken, ct).ConfigureAwait(false);
            return true;
        }
        catch (SteamLoginException ex) when (ex.Result is EResult.AccessDenied or EResult.InvalidPassword or EResult.Expired or EResult.Revoked)
        {
            Log.Warn($"La sesión guardada de Steam ya no es válida ({ex.Result}); hay que volver a iniciar sesión.");
            _store.Clear();
            SetState(SteamSessionState.Failed, "La sesión de Steam ha caducado. Vuelve a iniciar sesión en Ajustes.");
            return false;
        }
    }

    /// <summary>Sesión anónima: solo contenido público (útil para pruebas y herramientas gratuitas).</summary>
    public async Task LoginAnonymousAsync(CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        SetState(SteamSessionState.LoggingIn, "Iniciando sesión anónima…");
        var tcs = _loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        _user!.LogOnAnonymous();
        var result = await tcs.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
        if (result.Result != EResult.OK) throw new SteamLoginException(result.Result);
        IsAnonymous = true;
        AccountName = null;
        _licenses.TrySetResult([]);
        SetState(SteamSessionState.LoggedIn, "Sesión anónima iniciada.");
    }

    /// <summary>Cierra la sesión y borra el token guardado.</summary>
    public async Task LogOutAsync()
    {
        _store.Clear();
        AccountName = null;
        SteamId = null;
        await DisconnectAsync().ConfigureAwait(false);
        SetState(SteamSessionState.Disconnected, "Sesión de Steam cerrada.");
    }

    private async Task LogOnWithTokenAsync(string accountName, string refreshToken, CancellationToken ct)
    {
        var tcs = _loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        _licenses = NewLicenseTcs();
        _user!.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken,
            ShouldRememberPassword = true,
            LoginID = LauncherLoginId,
        });

        var result = await tcs.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
        if (result.Result != EResult.OK) throw new SteamLoginException(result.Result);

        IsAnonymous = false;
        AccountName = accountName;
        SteamId = result.ClientSteamID ?? _client!.SteamID;
        CellId = result.CellID;
        SetState(SteamSessionState.LoggedIn, $"Conectado como {accountName}.");
    }

    // =====================================================================================
    // Consultas
    // =====================================================================================

    /// <summary>Licencias de la cuenta (Steam las envía justo después de iniciar sesión).</summary>
    public Task<IReadOnlyList<SteamApps.LicenseListCallback.License>> GetLicensesAsync(CancellationToken ct) =>
        _licenses.Task.WaitAsync(ConnectTimeout, ct);

    /// <summary>Información PICS de apps (KeyValues «appinfo»), pidiendo antes los tokens de acceso.</summary>
    public async Task<Dictionary<uint, KeyValue>> GetAppInfoAsync(IEnumerable<uint> appIds, CancellationToken ct)
    {
        var ids = appIds.Distinct().ToList();
        var result = new Dictionary<uint, KeyValue>();
        if (ids.Count == 0) return result;
        Touch();

        foreach (var batch in ids.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            var tokens = await _apps!.PICSGetAccessTokens(batch, []);
            var requests = batch.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id)));
            var info = await _apps.PICSGetProductInfo(requests, []);
            foreach (var cb in info.Results ?? [])
                foreach (var (id, app) in cb.Apps)
                    result[id] = app.KeyValues;
        }
        return result;
    }

    /// <summary>Información PICS de paquetes (licencias) → qué apps contiene cada uno.</summary>
    public async Task<Dictionary<uint, KeyValue>> GetPackageInfoAsync(IEnumerable<(uint Id, ulong Token)> packages, CancellationToken ct)
    {
        var result = new Dictionary<uint, KeyValue>();
        Touch();
        foreach (var batch in packages.DistinctBy(p => p.Id).Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            var info = await _apps!.PICSGetProductInfo([], batch.Select(p => new SteamApps.PICSRequest(p.Id, p.Token)));
            foreach (var cb in info.Results ?? [])
                foreach (var (id, pkg) in cb.Packages)
                    result[id] = pkg.KeyValues;
        }
        return result;
    }

    public async Task<byte[]?> GetDepotKeyAsync(uint depotId, uint appId)
    {
        Touch();
        var cb = await _apps!.GetDepotDecryptionKey(depotId, appId);
        return cb.Result == EResult.OK ? cb.DepotKey : null;
    }

    public Task<ulong> GetManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
    {
        Touch();
        return _content!.GetManifestRequestCode(depotId, appId, manifestId, branch);
    }

    /// <summary>Servidores CDN (SteamPipe) para descargar, ordenados por carga.</summary>
    public async Task<IReadOnlyList<Server>> GetCdnServersAsync(uint appId)
    {
        Touch();
        var servers = await _content!.GetServersForSteamPipe(CellId == 0 ? null : CellId).ConfigureAwait(false);
        return servers
            .Where(s => s.Type is "SteamCache" or "CDN")
            .Where(s => s.AllowedAppIds is null || s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(appId))
            .OrderBy(s => s.WeightedLoad)
            .ToList();
    }

    /// <summary>Cliente CDN ligado a esta conexión (el llamador lo libera).</summary>
    public Client CreateCdnClient()
    {
        Touch();
        return new Client(_client!);
    }

    /// <summary>Pide a Steam la licencia gratuita de una app (juegos gratuitos aún no añadidos a la cuenta).</summary>
    public async Task<bool> RequestFreeLicenseAsync(uint appId)
    {
        Touch();
        var cb = await _apps!.RequestFreeLicense(appId);
        return cb.Result == EResult.OK && (cb.GrantedApps.Contains(appId) || cb.GrantedPackages.Count > 0);
    }

    /// <summary>
    /// Progreso de logros del usuario en un juego (servicio Player de Steam) y sus logros más destacados
    /// (con nombre, descripción e icono). Null si no hay sesión o Steam no responde.
    /// </summary>
    public async Task<AchievementSummary?> GetAchievementsAsync(uint appId, int maxTop, CancellationToken ct)
    {
        try
        {
            if (!await LoginWithSavedSessionAsync(ct).ConfigureAwait(false) || SteamId is null) return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin conexión o Steam ocupado (p. ej. NoConnection): sin logros, sin excepción sin observar.
            Log.Warn($"Steam (red): logros no disponibles ahora ({ex.Message}).");
            return null;
        }
        Hold();
        try
        {
            var player = _client!.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
            var steamId = SteamId.ConvertToUInt64();

            var progressRequest = new CPlayer_GetAchievementsProgress_Request { steamid = steamId, language = "spanish" };
            progressRequest.appids.Add(appId);
            var progress = await player.GetAchievementsProgress(progressRequest).ToTask().WaitAsync(ct).ConfigureAwait(false);
            if (progress.Result != EResult.OK) return null;
            var entry = progress.Body.achievement_progress.FirstOrDefault(a => a.appid == appId);
            if (entry is null || entry.total == 0) return new AchievementSummary(0, 0, []);

            var top = new List<AchievementInfo>();
            if (entry.unlocked > 0 && maxTop > 0)
            {
                var topRequest = new CPlayer_GetTopAchievementsForGames_Request
                {
                    steamid = steamId,
                    language = "spanish",
                    max_achievements = (uint)maxTop,
                };
                topRequest.appids.Add(appId);
                var topResponse = await player.GetTopAchievementsForGames(topRequest).ToTask().WaitAsync(ct).ConfigureAwait(false);
                if (topResponse.Result == EResult.OK)
                {
                    foreach (var a in topResponse.Body.games.FirstOrDefault(g => g.appid == appId)?.achievements ?? [])
                    {
                        var icon = string.IsNullOrEmpty(a.icon) ? null
                            : $"https://cdn.fastly.steamstatic.com/steamcommunity/public/images/apps/{appId}/{a.icon}";
                        top.Add(new AchievementInfo(a.name ?? string.Empty, a.desc ?? string.Empty, icon, a.player_percent_unlocked));
                    }
                }
            }
            return new AchievementSummary(entry.unlocked, entry.total, top);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam (red): no se pudieron leer los logros de {appId}: {ex.Message}");
            return null;
        }
        finally
        {
            Release();
        }
    }

    // =====================================================================================
    // Vida de la conexión
    // =====================================================================================

    /// <summary>Mantiene la conexión abierta (descargas en curso). Devolver con <see cref="Release"/>.</summary>
    public void Hold() { Interlocked.Increment(ref _holds); Touch(); }

    public void Release() { Interlocked.Decrement(ref _holds); Touch(); }

    private void Touch() => _idleTimer.Change(IdleDisconnect, Timeout.InfiniteTimeSpan);

    private async Task DisconnectIfIdleAsync()
    {
        if (Volatile.Read(ref _holds) > 0) { Touch(); return; }
        Log.Info("Steam (red): desconectando por inactividad.");
        await DisconnectAsync().ConfigureAwait(false);
        ProcessUtilsTrim();
    }

    private static void ProcessUtilsTrim() => Core.ProcessUtils.TrimCurrentProcess();

    private async Task ConnectAsync(CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Touch();
            if (_client?.IsConnected == true) return;

            SetState(SteamSessionState.Connecting, "Conectando con los servidores de Steam…");
            var configuration = SteamConfiguration.Create(b => b.WithConnectionTimeout(ConnectTimeout));
            _client = new SteamClient(configuration, "Purgine");
            _callbacks = new CallbackManager(_client);
            _user = _client.GetHandler<SteamUser>();
            _apps = _client.GetHandler<SteamApps>();
            _content = _client.GetHandler<SteamContent>();

            _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected?.TrySetResult(true));
            _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => _loggedOn?.TrySetResult(cb));
            _callbacks.Subscribe<SteamUser.LoggedOffCallback>(cb =>
            {
                Log.Warn($"Steam cerró la sesión: {cb.Result}");
                SetState(SteamSessionState.Disconnected, $"Steam cerró la sesión ({cb.Result}).");
            });
            _callbacks.Subscribe<SteamApps.LicenseListCallback>(cb =>
            {
                if (cb.Result == EResult.OK) _licenses.TrySetResult(cb.LicenseList);
            });

            _pumpCts = new CancellationTokenSource();
            var callbacks = _callbacks;
            var token = _pumpCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await callbacks.RunWaitCallbackAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.Warn($"Steam (red): error en callback: {ex.Message}"); }
                }
            }, token);

            _client.Connect();
            await _connected.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
            Log.Info("Steam (red): conectado a un servidor CM.");
        }
        catch
        {
            SetState(SteamSessionState.Failed, "No se pudo conectar con Steam. Comprueba tu conexión.");
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        _connected?.TrySetResult(false);
        _loggedOn?.TrySetException(new SteamLoginException(EResult.NoConnection));
        if (!cb.UserInitiated) Log.Warn("Steam (red): conexión perdida.");
        SetState(SteamSessionState.Disconnected, cb.UserInitiated ? "Desconectado de Steam." : "Se perdió la conexión con Steam.");
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is null) return;
            try
            {
                if (_user is not null && _client.IsConnected && !IsAnonymous) _user.LogOff();
                _client.Disconnect();
            }
            catch (Exception ex) { Log.Warn($"Steam (red): error al desconectar: {ex.Message}"); }
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _pumpCts = null;
            _client = null;
            _callbacks = null;
            _user = null;
            _apps = null;
            _content = null;
            IsAnonymous = false;
            _licenses = NewLicenseTcs();
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void SetState(SteamSessionState state, string message)
    {
        lock (_gate) _state = state;
        StateChanged?.Invoke(state, message);
    }

    private static TaskCompletionSource<IReadOnlyList<SteamApps.LicenseListCallback.License>> NewLicenseTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
    }
}

/// <summary>Steam rechazó el inicio de sesión.</summary>
/// <summary>Logros de un juego: desbloqueados / total y los más destacados del usuario.</summary>
public sealed record AchievementSummary(uint Unlocked, uint Total, IReadOnlyList<AchievementInfo> Top);

/// <summary>Un logro desbloqueado (icono en la CDN de Steam; porcentaje global de jugadores que lo tienen).</summary>
public sealed record AchievementInfo(string Name, string Description, string? IconUrl, string? GlobalPercent);

public sealed class SteamLoginException(EResult result)
    : Exception($"Steam rechazó el inicio de sesión: {result}")
{
    public EResult Result { get; } = result;
}
