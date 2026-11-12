using System.Windows.Input;

namespace Purgine.ViewModels;

/// <summary>Plataforma en la que se canjea una clave.</summary>
public enum RedeemPlatform { Steam, Epic }

/// <summary>Petición de canje para la vista (rellenar la página oficial y, si procede, enviarla).</summary>
public sealed record RedeemRequest(RedeemPlatform Platform, string Key, bool Submit);

/// <summary>
/// Pestaña Tienda: la web oficial de Steam dentro de un WebView2 (lo gestiona <c>StoreView</c>).
/// Esta clase decide qué cargar y qué hacer con los enlaces <c>steam://</c> de la web (instalar, jugar…),
/// que se resuelven con el motor nativo de Purgine en lugar de despertar el cliente de Steam.
/// </summary>
public sealed class StoreViewModel : ObservableObject
{
    public const string HomeUrl = "https://store.steampowered.com/?l=spanish";
    public const string EpicHomeUrl = "https://store.epicgames.com/es-ES/";
    public const string ItchHomeUrl = "https://itch.io/";

    /// <summary>Búsqueda en Epic Games Store (Epic no ofrece un catálogo público para enlazar la ficha exacta).</summary>
    public static string EpicSearchUrl(string name) => $"https://store.epicgames.com/es-ES/browse?q={Uri.EscapeDataString(name)}";

    /// <summary>Tienda mostrada: Steam, Epic Games Store o itch.io (el mismo navegador, para no duplicar procesos).</summary>
    public enum StoreKind { Steam, Epic, Itch }

    private StoreKind _kind;

    public StoreKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            RaiseKind();
            Navigate(CurrentHomeUrl);
        }
    }

    private void RaiseKind()
    {
        OnPropertyChanged(nameof(IsSteamStore));
        OnPropertyChanged(nameof(IsEpicStore));
        OnPropertyChanged(nameof(IsItchStore));
    }

    public bool IsSteamStore { get => _kind == StoreKind.Steam; set { if (value) Kind = StoreKind.Steam; } }
    public bool IsEpicStore { get => _kind == StoreKind.Epic; set { if (value) Kind = StoreKind.Epic; } }
    public bool IsItchStore { get => _kind == StoreKind.Itch; set { if (value) Kind = StoreKind.Itch; } }

    public string CurrentHomeUrl => _kind switch { StoreKind.Epic => EpicHomeUrl, StoreKind.Itch => ItchHomeUrl, _ => HomeUrl };

    /// <summary>La página actual pertenece a otra tienda: el selector se ajusta sin recargar.</summary>
    public void SyncStoreFromUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        StoreKind? kind = u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase) ? StoreKind.Epic
            : u.Host.EndsWith("itch.io", StringComparison.OrdinalIgnoreCase) ? StoreKind.Itch
            : u.Host.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ? StoreKind.Steam
            : null;
        if (kind is { } k && k != _kind)
        {
            _kind = k;
            RaiseKind();
        }
    }

    /// <summary>Abre una página de itch.io en la tienda.</summary>
    public void ShowItch(string url)
    {
        _kind = StoreKind.Itch;
        RaiseKind();
        Navigate(url);
    }

    /// <summary>La vista capturó una descarga de itch.io.</summary>
    public void CaptureItchDownload(ItchCapturedDownload download) => _actions.DownloadFromItch(download);

    private readonly IStoreActions _actions;
    private bool _isActive;
    private string? _pendingUrl;

    public StoreViewModel(IStoreActions actions)
    {
        _actions = actions;
        RedeemCommand = new RelayCommand(_ => Redeem(), _ => !IsRedeeming && NormalizedKey.Length >= 5);
    }

    // =====================================================================================
    // Canjear clave (Steam / Epic Games) en el mismo navegador, con la sesión de la tienda
    // =====================================================================================

    public const string SteamRedeemUrl = "https://store.steampowered.com/account/registerkey?l=spanish";
    public const string EpicRedeemUrl = "https://store.epicgames.com/es-ES/redeem";
    public const string SteamSubscriberAgreementUrl = "https://store.steampowered.com/subscriber_agreement/?l=spanish";

    private bool _isRedeemMode;
    private RedeemPlatform _redeemPlatform = RedeemPlatform.Steam;
    private string _redeemKey = string.Empty;
    private bool _acceptSteamAgreement;
    private bool _isRedeeming;
    private string _redeemStatus = string.Empty;
    private bool _redeemSucceeded;

    /// <summary>Pestaña «Canjear»: panel de canje encima del navegador, que muestra la página oficial de canje.</summary>
    public bool IsRedeemMode
    {
        get => _isRedeemMode;
        set
        {
            if (!SetProperty(ref _isRedeemMode, value)) return;
            RedeemStatus = string.Empty;
            Navigate(value ? RedeemUrl : CurrentHomeUrl);
        }
    }

    public RedeemPlatform RedeemPlatform
    {
        get => _redeemPlatform;
        set
        {
            if (!SetProperty(ref _redeemPlatform, value)) return;
            OnPropertyChanged(nameof(IsSteamRedeem));
            OnPropertyChanged(nameof(IsEpicRedeem));
            OnPropertyChanged(nameof(KeyHint));
            RedeemStatus = string.Empty;
            if (_isRedeemMode) Navigate(RedeemUrl);
        }
    }

    public bool IsSteamRedeem { get => _redeemPlatform == RedeemPlatform.Steam; set { if (value) RedeemPlatform = RedeemPlatform.Steam; } }
    public bool IsEpicRedeem { get => _redeemPlatform == RedeemPlatform.Epic; set { if (value) RedeemPlatform = RedeemPlatform.Epic; } }

    public string RedeemUrl => _redeemPlatform == RedeemPlatform.Epic ? EpicRedeemUrl : SteamRedeemUrl;

    public string KeyHint => _redeemPlatform == RedeemPlatform.Epic
        ? "Código de Epic Games (p. ej. XXXXX-XXXXX-XXXXX-XXXXX)"
        : "Clave de producto de Steam (p. ej. AAAAA-BBBBB-CCCCC)";

    /// <summary>Clave escrita o pegada (se guarda en mayúsculas; los guiones se completan al canjear).</summary>
    public string RedeemKey
    {
        get => _redeemKey;
        set
        {
            if (!SetProperty(ref _redeemKey, (value ?? string.Empty).ToUpperInvariant())) return;
            OnPropertyChanged(nameof(NormalizedKey));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Clave limpia: sin espacios, en mayúsculas y, si se pegó sin guiones (15, 20 o 25 caracteres), agrupada de 5 en
    /// 5 como la escriben Steam y Epic.
    /// </summary>
    public string NormalizedKey
    {
        get
        {
            var raw = new string(_redeemKey.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            var alnum = new string(raw.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (!raw.Contains('-') && alnum.Length is 15 or 20 or 25)
                return string.Join('-', Enumerable.Range(0, alnum.Length / 5).Select(i => alnum.Substring(i * 5, 5)));
            return raw;
        }
    }

    /// <summary>Casilla del Acuerdo de Suscriptor de Steam: la marca el usuario (sin ella la clave solo se rellena).</summary>
    public bool AcceptSteamAgreement
    {
        get => _acceptSteamAgreement;
        set => SetProperty(ref _acceptSteamAgreement, value);
    }

    public bool IsRedeeming
    {
        get => _isRedeeming;
        set { if (SetProperty(ref _isRedeeming, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string RedeemStatus
    {
        get => _redeemStatus;
        private set { if (SetProperty(ref _redeemStatus, value)) OnPropertyChanged(nameof(HasRedeemStatus)); }
    }

    public bool HasRedeemStatus => _redeemStatus.Length > 0;

    public bool RedeemSucceeded
    {
        get => _redeemSucceeded;
        private set => SetProperty(ref _redeemSucceeded, value);
    }

    public ICommand RedeemCommand { get; }

    /// <summary>La vista rellena (y envía, si procede) la página oficial de canje.</summary>
    public event Action<RedeemRequest>? RedeemRequested;

    private void Redeem()
    {
        var submit = _redeemPlatform == RedeemPlatform.Epic || _acceptSteamAgreement;
        RedeemKey = NormalizedKey;
        RedeemSucceeded = false;
        RedeemStatus = submit ? "Canjeando…" : "Rellenando la clave en la página…";
        IsRedeeming = true;
        RedeemRequested?.Invoke(new RedeemRequest(_redeemPlatform, NormalizedKey, submit));
    }

    /// <summary>Resultado que comunica la vista.</summary>
    public void ReportRedeem(string status, bool succeeded = false)
    {
        IsRedeeming = false;
        RedeemSucceeded = succeeded;
        RedeemStatus = status;
        if (succeeded) RedeemKey = string.Empty;
    }

    /// <summary>La pestaña está a la vista. Al salir, la vista suspende el navegador y lo libera al rato.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    private bool _isCovered;

    /// <summary>
    /// Hay un diálogo encima (instalar): el navegador es una ventana nativa y se dibujaría sobre él, así que la
    /// vista lo oculta mientras tanto.
    /// </summary>
    public bool IsCovered
    {
        get => _isCovered;
        set => SetProperty(ref _isCovered, value);
    }

    /// <summary>La vista debe navegar a esta URL (si el navegador aún no existe, la recoge al crearlo).</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>La vista debe cerrar el navegador y liberar sus procesos (ventana en la bandeja, salida).</summary>
    public event Action? ReleaseRequested;

    public void Navigate(string url)
    {
        _pendingUrl = url;
        NavigationRequested?.Invoke(url);
    }

    public void ShowApp(string appId) => Navigate($"https://store.steampowered.com/app/{Uri.EscapeDataString(appId)}/?l=spanish");

    /// <summary>URL que la vista debe cargar al crear el navegador (y la olvida).</summary>
    public string? TakePendingUrl()
    {
        var url = _pendingUrl;
        _pendingUrl = null;
        return url;
    }

    public void Release() => ReleaseRequested?.Invoke();

    /// <summary>Un botón de la web de Epic quería abrir Epic Games Launcher (Purgine no lo abre).</summary>
    public void ReportEpicLauncherLink() =>
        _actions.ReportStatus("Purgine no abre Epic Games Launcher. Los juegos de Epic instalados se juegan desde la Biblioteca; " +
                              "las instalaciones de Epic aún se hacen con su launcher.");

    /// <summary>
    /// Enlaces <c>steam://</c> de la web. Devuelve la URL https a la que navegar en su lugar, o null si ya se
    /// atendió (instalar/jugar) o no se reconoce.
    /// </summary>
    public string? HandleSteamLink(string uri, string? pageTitle)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase))
            return null;

        // steam://run/440, steam://install/440, steam://openurl/https://…
        var command = parsed.Host.ToLowerInvariant();
        var rest = uri[(uri.IndexOf("//", StringComparison.Ordinal) + 2 + parsed.Host.Length)..].TrimStart('/');
        var appId = new string(rest.TakeWhile(char.IsAsciiDigit).ToArray());

        switch (command)
        {
            case "run" or "rungameid" or "launch" or "install" or "subscriptioninstall" when appId.Length > 0:
                _actions.PlayOrInstallFromStore(appId, CleanTitle(pageTitle), installOnly: command.Contains("install"));
                return null;
            case "store" when appId.Length > 0:
                return $"https://store.steampowered.com/app/{appId}/?l=spanish";
            case "openurl" or "openurl_external" when rest.StartsWith("http", StringComparison.OrdinalIgnoreCase):
                return Uri.UnescapeDataString(rest);
            default:
                _actions.ReportStatus("Ese enlace de Steam no se usa en Purgine (no hace falta el cliente para la tienda).");
                return null;
        }
    }

    /// <summary>«Comprar X en Steam» / «X on Steam» → «X» (solo se usa si el juego aún no se conoce).</summary>
    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        foreach (var suffix in new[] { " en Steam", " on Steam" })
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) title = title[..^suffix.Length];
        var discount = title.IndexOf("% en ", StringComparison.Ordinal);
        if (discount >= 0 && title.StartsWith("Ahorra", StringComparison.OrdinalIgnoreCase)) title = title[(discount + 5)..];
        return title.Trim();
    }
}
