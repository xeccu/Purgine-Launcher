using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LightLauncher.Infrastructure;
using LightLauncher.Store;

namespace LightLauncher.ViewModels;

/// <summary>Una fila de resultados u ofertas.</summary>
public sealed class StoreItemViewModel : ObservableObject
{
    private ImageSource? _image;
    private bool _isInstalled;

    private bool _isOwned;

    public StoreItemViewModel(StoreItem item, bool isInstalled, bool isOwned)
    {
        Item = item;
        _isInstalled = isInstalled;
        _isOwned = isOwned;
    }

    /// <summary>Consta como propio pero no está instalado.</summary>
    public bool IsOwnedNotInstalled => _isOwned && !_isInstalled;

    public void SetState(bool installed, bool owned)
    {
        IsInstalled = installed;
        _isOwned = owned;
        OnPropertyChanged(nameof(IsOwnedNotInstalled));
    }

    public StoreItem Item { get; }
    public string Name => Item.Name;
    public string? PriceText => Item.PriceText;
    public string? OriginalPriceText => Item.OriginalPriceText;
    public bool HasDiscount => Item.DiscountPercent > 0;
    public string DiscountText => $"-{Item.DiscountPercent}%";

    public bool IsInstalled
    {
        get => _isInstalled;
        set => SetProperty(ref _isInstalled, value);
    }

    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public async Task LoadImageAsync(SteamStoreService store, CancellationToken ct)
    {
        if (_image is not null || Item.ImageUrl is null) return;
        var bytes = await store.GetImageBytesAsync(Item.ImageUrl, ct);
        if (bytes is not null) Image = await Task.Run(() => ImageLoader.FromBytes(bytes, 184), ct);
    }
}

/// <summary>Ficha de producto con las acciones del puente (instalar / comprar / jugar).</summary>
public sealed class StoreDetailsViewModel : ObservableObject
{
    private readonly IStoreActions _actions;
    private ImageSource? _headerImage;
    private GameCardViewModel? _installed;

    private bool _isOwned;

    public StoreDetailsViewModel(StoreAppDetails details, IStoreActions actions)
    {
        Details = details;
        _actions = actions;
        _installed = actions.FindInstalled(details.AppId);
        _isOwned = actions.IsKnownOwned(details.AppId);

        InstallCommand = new RelayCommand(
            async _ => await _actions.InstallAsync(Details.AppId, Details.Name, Details.IsFree),
            _ => CanInstall);
        // Compra 100 % web: la ficha oficial en el navegador, sin abrir el cliente de Steam.
        BuyCommand = new RelayCommand(_ => _actions.OpenInBrowser(Details.AppId));
        CartCommand = new RelayCommand(_ => _actions.OpenCart());
        PlayCommand = new RelayCommand(_ => { if (_installed is not null) _actions.Play(_installed); }, _ => _installed is not null);
        BrowserCommand = new RelayCommand(_ => _actions.OpenInBrowser(Details.AppId));
    }

    public StoreAppDetails Details { get; }
    public string Name => Details.Name;
    public string? Description => Details.Description;
    public string? PriceText => Details.PriceText ?? (Details.ComingSoon ? "Próximamente" : null);
    public string? OriginalPriceText => Details.OriginalPriceText;
    public bool HasDiscount => Details.DiscountPercent > 0;
    public string DiscountText => $"-{Details.DiscountPercent}%";

    public string MetaLine => string.Join("  ·  ", new[]
    {
        Details.Genres,
        Details.ReleaseDate is { Length: > 0 } d ? (Details.ComingSoon ? $"Sale: {d}" : d) : null,
        Details.Metacritic is int m ? $"Metacritic {m}" : null,
        Details.SupportsWindows ? null : "Sin versión para Windows",
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string? Developers => Details.Developers is { Length: > 0 } d ? $"Desarrollo: {d}" : null;

    public ImageSource? HeaderImage
    {
        get => _headerImage;
        private set => SetProperty(ref _headerImage, value);
    }

    public bool IsInstalled => _installed is not null;
    public bool IsOwned => _isOwned;

    private bool Installable => !IsInstalled && !Details.ComingSoon && Details.SupportsWindows;

    /// <summary>
    /// Solo se ofrece <c>steam://install</c> si el juego es gratuito o consta como propio: así nunca se
    /// llega al error «no hay licencias» dentro del cliente de Steam.
    /// </summary>
    public bool CanInstall => Installable && (Details.IsFree || _isOwned);

    /// <summary>De pago y sin constancia de licencia: hay que comprarlo primero (en la web).</summary>
    public bool NeedsPurchase => !IsInstalled && !Details.IsFree && !_isOwned;

    /// <summary>Botón «Instalar» deshabilitado: de pago y sin licencia en la cuenta.</summary>
    public bool ShowLockedInstall => NeedsPurchase && !Details.ComingSoon;

    public string InstallText => _isOwned || !Details.IsFree ? "Instalar" : "Instalar gratis";

    public string OwnershipText =>
        IsInstalled ? (_installed is { IsDownloading: true } c ? c.DownloadText : "Instalado en tu biblioteca")
        : _isOwned ? "En tu cuenta · no instalado"
        : Details.IsFree ? "Gratuito"
        : Details.ComingSoon ? "Próximamente"
        : "No consta en tu cuenta en este equipo";

    public string BuyText => Details.ComingSoon ? "Ver en la web" : "Comprar en la web";

    public string InstalledText => OwnershipText;

    public ICommand InstallCommand { get; }
    public ICommand BuyCommand { get; }
    public ICommand CartCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand BrowserCommand { get; }

    public void RefreshInstalledState()
    {
        _installed = _actions.FindInstalled(Details.AppId);
        _isOwned = _actions.IsKnownOwned(Details.AppId);
        OnPropertyChanged(string.Empty);
    }

    public async Task LoadImageAsync(SteamStoreService store, CancellationToken ct)
    {
        if (Details.HeaderImageUrl is null) return;
        var bytes = await store.GetImageBytesAsync(Details.HeaderImageUrl, ct);
        if (bytes is not null) HeaderImage = await Task.Run(() => ImageLoader.FromBytes(bytes, 460), ct);
    }
}

/// <summary>
/// Pestaña Tienda: búsqueda, ofertas y fichas de producto de la tienda de Steam, renderizadas en WPF
/// nativo (sin navegador embebido). Comprar e instalar se delegan a Steam vía <c>steam://</c>.
/// </summary>
public sealed class StoreViewModel : ObservableObject
{
    private readonly SteamStoreService _store;
    private readonly IStoreActions _actions;
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailsCts;
    private bool _initialized;
    private string _query = string.Empty;
    private string _listTitle = "Ofertas destacadas";
    private bool _isLoading;
    private bool _isDetailsLoading;
    private string? _errorText;
    private StoreDetailsViewModel? _details;

    public StoreViewModel(SteamStoreService store, IStoreActions actions)
    {
        _store = store;
        _actions = actions;
        SearchCommand = new RelayCommand(async _ => await SearchAsync(), _ => !string.IsNullOrWhiteSpace(Query));
        SpecialsCommand = new RelayCommand(async _ => await LoadSpecialsAsync());
        SelectCommand = new RelayCommand(async p => { if (p is StoreItemViewModel i) await ShowDetailsAsync(i.Item.AppId); });
        CloseDetailsCommand = new RelayCommand(_ => Details = null);
    }

    public ObservableCollection<StoreItemViewModel> Items { get; } = [];

    public ICommand SearchCommand { get; }
    public ICommand SpecialsCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand CloseDetailsCommand { get; }

    public string Query
    {
        get => _query;
        set => SetProperty(ref _query, value ?? string.Empty);
    }

    public string ListTitle
    {
        get => _listTitle;
        private set => SetProperty(ref _listTitle, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsDetailsLoading
    {
        get => _isDetailsLoading;
        private set => SetProperty(ref _isDetailsLoading, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public StoreDetailsViewModel? Details
    {
        get => _details;
        private set { if (SetProperty(ref _details, value)) OnPropertyChanged(nameof(HasDetails)); }
    }

    public bool HasDetails => _details is not null;

    public string RegionNote =>
        $"Precios de la tienda de Steam · región {_store.CountryCode}. La compra se hace en la web oficial de Steam, en tu navegador: LightLauncher nunca ve tu cuenta ni tus datos de pago.";

    /// <summary>Primera visita a la pestaña: carga ofertas (no antes, para no usar red sin necesidad).</summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await LoadSpecialsAsync();
    }

    public Task SearchAsync()
    {
        var term = Query.Trim();
        return term.Length == 0
            ? LoadSpecialsAsync()
            : LoadListAsync($"Resultados para «{term}»", ct => _store.SearchAsync(term, ct));
    }

    public Task LoadSpecialsAsync() => LoadListAsync("Ofertas destacadas", _store.GetSpecialsAsync);

    private async Task LoadListAsync(string title, Func<CancellationToken, Task<IReadOnlyList<StoreItem>>> fetch)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorText = null;
        ListTitle = title;
        try
        {
            var results = await fetch(cts.Token);
            if (cts.IsCancellationRequested) return;

            Items.Clear();
            foreach (var r in results)
                Items.Add(new StoreItemViewModel(r, _actions.FindInstalled(r.AppId) is not null, _actions.IsKnownOwned(r.AppId)));
            if (Items.Count == 0) ErrorText = "No se ha encontrado nada.";

            IsLoading = false;
            foreach (var item in Items.ToList())
            {
                if (cts.IsCancellationRequested) break;
                await item.LoadImageAsync(_store, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* sustituida por otra búsqueda */ }
        catch (Exception ex)
        {
            Log.Warn($"Tienda: {ex.Message}");
            ErrorText = "No se pudo conectar con la tienda de Steam. Comprueba tu conexión e inténtalo de nuevo.";
        }
        finally
        {
            if (ReferenceEquals(_listCts, cts)) IsLoading = false;
        }
    }

    /// <summary>Abre la ficha de un juego (también se usa desde «Ver en la tienda» de la biblioteca).</summary>
    public async Task ShowDetailsAsync(string appId)
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        IsDetailsLoading = true;
        try
        {
            var details = await _store.GetDetailsAsync(appId, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (details is null)
            {
                ErrorText = "Esa ficha no está disponible en tu región.";
                Details = null;
                return;
            }
            var vm = new StoreDetailsViewModel(details, _actions);
            Details = vm;
            IsDetailsLoading = false;
            await vm.LoadImageAsync(_store, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Tienda (ficha {appId}): {ex.Message}");
            ErrorText = "No se pudo cargar la ficha del juego.";
        }
        finally
        {
            if (ReferenceEquals(_detailsCts, cts)) IsDetailsLoading = false;
        }
    }

    /// <summary>La biblioteca cambió (instalación nueva / desinstalación): actualizar marcas «Instalado».</summary>
    public void RefreshInstalledState()
    {
        foreach (var item in Items)
            item.SetState(_actions.FindInstalled(item.Item.AppId) is not null, _actions.IsKnownOwned(item.Item.AppId));
        _details?.RefreshInstalledState();
    }

    /// <summary>Al ocultarse en la bandeja: cancelar descargas en curso y soltar la pila de red.</summary>
    public void OnHidden()
    {
        _listCts?.Cancel();
        _detailsCts?.Cancel();
        _store.ReleaseNetworkResources();
    }
}
