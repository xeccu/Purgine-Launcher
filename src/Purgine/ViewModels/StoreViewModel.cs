namespace Purgine.ViewModels;

/// <summary>
/// Pestaña Tienda: la web oficial de Steam dentro de un WebView2 (lo gestiona <c>StoreView</c>).
/// Esta clase decide qué cargar y qué hacer con los enlaces <c>steam://</c> de la web (instalar, jugar…),
/// que se resuelven con el motor nativo de Purgine en lugar de despertar el cliente de Steam.
/// </summary>
public sealed class StoreViewModel : ObservableObject
{
    public const string HomeUrl = "https://store.steampowered.com/?l=spanish";

    private readonly IStoreActions _actions;
    private bool _isActive;
    private string? _pendingUrl;

    public StoreViewModel(IStoreActions actions) => _actions = actions;

    /// <summary>La pestaña está a la vista. Al salir, la vista suspende el navegador y lo libera al rato.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
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
