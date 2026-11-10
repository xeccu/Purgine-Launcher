using System.Collections.ObjectModel;
using System.Windows.Input;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.SteamNet;

namespace Purgine.ViewModels;

/// <summary>Un DLC de la cuenta para este juego.</summary>
public sealed class DlcItemViewModel : ObservableObject
{
    private bool _isEnabled;

    public DlcItemViewModel(uint id, string name, bool hasContent, bool isInstalled, bool isEnabled)
    {
        Id = id;
        Name = name;
        HasContent = hasContent;
        IsInstalled = isInstalled;
        _isEnabled = isEnabled;
        InitialEnabled = isEnabled;
    }

    public uint Id { get; }
    public string Name { get; }
    /// <summary>Tiene archivos propios (depot): se puede activar o desactivar.</summary>
    public bool HasContent { get; }
    public bool IsInstalled { get; }
    public bool InitialEnabled { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Detail =>
        !HasContent ? "Sin archivos propios: lo activa tu licencia (no se puede desactivar)"
        : IsInstalled ? "Instalado" : "No instalado";
}

/// <summary>
/// Un mod de Workshop. Marcar o desmarcar la casilla lo activa o desactiva en disco al momento (como el juego
/// lo lee); si falla, la casilla vuelve a su estado y se informa.
/// </summary>
public sealed class ModItemViewModel : ObservableObject
{
    private readonly Func<WorkshopMod, bool, string?> _apply;
    private WorkshopMod _mod;
    private bool _isEnabled;
    private string _title;

    public ModItemViewModel(WorkshopMod mod, Func<WorkshopMod, bool, string?> apply)
    {
        _mod = mod;
        _apply = apply;
        _title = mod.Title ?? $"Mod {mod.Id}";
        SizeText = Formatting.Bytes(mod.SizeBytes);
        _isEnabled = mod.Enabled;
    }

    public ulong Id => _mod.Id;
    public string SizeText { get; }
    public bool HasTitleFromDisk => _mod.Title is not null;

    /// <summary>«Carpeta de mods del juego (disable.it)» / «Carpeta de Workshop».</summary>
    public string ControlText => _mod.Control == ModControl.GameModsFolder ? "mods del juego" : "Workshop";

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            var error = _apply(_mod, value);
            if (error is null)
            {
                _isEnabled = value;
                _mod = _mod with { Enabled = value };
            }
            OnPropertyChanged(); // si falló, la casilla vuelve a su estado real
        }
    }

    /// <summary>Tras mover la carpeta (mods de Workshop) la ruta cambia.</summary>
    public void UpdatePath(string path) => _mod = _mod with { Path = path };
}

/// <summary>
/// Propiedades de un juego instalado: DLC (activar/desactivar los que tienen archivos propios, guardado en el
/// appmanifest como hace el cliente de Steam) y mods de Workshop (activar/desactivar moviendo su carpeta).
/// Los cambios se aplican al pulsar «Aplicar».
/// </summary>
public sealed class GamePropertiesViewModel : ObservableObject
{
    private readonly GameCardViewModel _card;
    private readonly SteamSession _session;
    private readonly SteamOwnershipService _ownership;
    private readonly SteamCommunityService _community;
    private bool _isDlcTab = true;
    private string _dlcStatus = "Cargando los DLC…";
    private string _modsStatus = "Buscando mods…";
    private string _resultText = string.Empty;
    private bool _isBusy;

    public GamePropertiesViewModel(GameCardViewModel card, SteamSession session, SteamOwnershipService ownership, SteamCommunityService community)
    {
        _card = card;
        _session = session;
        _ownership = ownership;
        _community = community;
        ApplyCommand = new RelayCommand(_ => Apply(), _ => !_isBusy && HasChanges);
        OpenWorkshopCommand = new RelayCommand(_ => OpenUrl($"https://steamcommunity.com/app/{card.Entry.AppId}/workshop/"));
        OpenSubscriptionsCommand = new RelayCommand(_ => OpenUrl(
            $"https://steamcommunity.com/my/myworkshopfiles/?appid={card.Entry.AppId}&browsefilter=mysubscriptions"));
    }

    public string Title => $"Propiedades — {_card.Name}";
    public string GameName => _card.Name;

    public bool IsDlcTab { get => _isDlcTab; set { if (SetProperty(ref _isDlcTab, value)) OnPropertyChanged(nameof(IsModsTab)); } }
    public bool IsModsTab { get => !_isDlcTab; set => IsDlcTab = !value; }

    public ObservableCollection<DlcItemViewModel> Dlcs { get; } = [];
    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    public string DlcStatus { get => _dlcStatus; private set => SetProperty(ref _dlcStatus, value); }
    public string ModsStatus { get => _modsStatus; private set => SetProperty(ref _modsStatus, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public string DlcTabHeader => $"DLC ({Dlcs.Count})";
    public string ModsTabHeader => $"Workshop ({Mods.Count})";

    /// <summary>Solo los DLC esperan a «Aplicar» (necesitan Steam cerrado); los mods se aplican al momento.</summary>
    public bool HasChanges => Dlcs.Any(d => d.HasContent && d.IsEnabled != d.InitialEnabled);

    public ICommand ApplyCommand { get; }
    public ICommand OpenWorkshopCommand { get; }
    public ICommand OpenSubscriptionsCommand { get; }

    private string? Library => _card.Entry.LibraryPath;
    private string AcfPath => Path.Combine(Library!, "steamapps", $"appmanifest_{_card.Entry.AppId}.acf");

    // =====================================================================================

    public async Task LoadAsync()
    {
        await Task.WhenAll(LoadDlcsAsync(), LoadModsAsync());
    }

    private async Task LoadDlcsAsync()
    {
        if (!uint.TryParse(_card.Entry.AppId, out var appId) || Library is null) { DlcStatus = "Solo disponible para juegos de Steam instalados."; return; }
        if (!_session.HasSavedSession) { DlcStatus = "Inicia sesión en Steam (Ajustes) para ver los DLC."; return; }

        var info = await Task.Run(async () =>
        {
            try
            {
                if (!await _session.LoginWithSavedSessionAsync(CancellationToken.None)) return null;
                return (await _session.GetAppInfoAsync([appId], CancellationToken.None)).GetValueOrDefault(appId);
            }
            catch (Exception ex)
            {
                Log.Warn($"Propiedades: no se pudo leer la info de {appId}: {ex.Message}");
                return null;
            }
        });
        if (info is null) { DlcStatus = "No se pudo consultar Steam (¿sin conexión?)."; return; }

        var listed = (info["extended"]["listofdlc"].Value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => uint.TryParse(s, out var id) ? id : 0).Where(id => id != 0).ToHashSet();
        var withDepots = info["depots"].Children
            .Select(d => d["dlcappid"].AsUnsignedInteger()).Where(id => id != 0).ToHashSet();
        listed.UnionWith(withDepots);

        var installed = InstalledDlcDepots();
        var disabled = AppManifestWriter.ReadDisabledDlc(AcfPath);
        var owned = listed.Where(_ownership.IsOwned).ToList();

        foreach (var id in owned.OrderBy(id => _ownership.Find(id)?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = _ownership.Find(id)?.Name ?? $"DLC {id}";
            var item = new DlcItemViewModel(id, name, withDepots.Contains(id), installed.Contains(id), !disabled.Contains(id));
            item.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
            Dlcs.Add(item);
        }
        OnPropertyChanged(nameof(DlcTabHeader));
        var notOwned = listed.Count - owned.Count;
        DlcStatus = owned.Count == 0
            ? listed.Count == 0 ? "Este juego no tiene DLC." : $"No tienes ninguno de sus {listed.Count} DLC."
            : $"Tienes {owned.Count} DLC" + (notOwned > 0 ? $" ({notOwned} más en la tienda)." : ".") +
              " Desactivar un DLC con archivos hace que Steam y Purgine dejen de instalarlo y actualizarlo; " +
              "se aplica en la próxima actualización o verificación. Requiere Steam cerrado.";
    }

    /// <summary>DLC con depot instalado (InstalledDepots/…/dlcappid del appmanifest).</summary>
    private HashSet<uint> InstalledDlcDepots()
    {
        try
        {
            var depots = VdfParser.ParseFile(AcfPath)["AppState"]?["InstalledDepots"];
            return depots is null ? [] : depots.Children.Values
                .Select(d => uint.TryParse(d.GetValue("dlcappid"), out var id) ? id : 0).Where(id => id != 0).ToHashSet();
        }
        catch (Exception) { return []; }
    }

    private async Task LoadModsAsync()
    {
        if (Library is null) { ModsStatus = "Solo disponible para juegos instalados."; return; }
        var mods = await Task.Run(() => WorkshopModManager.List(Library, _card.Entry.AppId, _card.Entry.InstallDir));
        foreach (var m in mods.OrderByDescending(m => m.Enabled).ThenBy(m => m.Title ?? "~", StringComparer.CurrentCultureIgnoreCase))
            Mods.Add(new ModItemViewModel(m, ApplyMod));
        OnPropertyChanged(nameof(ModsTabHeader));
        if (mods.Count == 0) { ModsStatus = "No hay mods de Workshop descargados para este juego."; return; }
        UpdateModsStatus();

        // Nombres que no estén en el disco: se piden a Steam.
        var unnamed = Mods.Where(m => !m.HasTitleFromDisk).Select(m => m.Id).ToList();
        if (unnamed.Count > 0 && _session.HasSavedSession)
        {
            var titles = await Task.Run(() => _community.GetWorkshopTitlesAsync(unnamed, CancellationToken.None));
            foreach (var item in Mods)
                if (titles.TryGetValue(item.Id, out var title)) item.Title = title;
        }
    }

    private void UpdateModsStatus()
    {
        var gameFolder = Mods.Any(m => m.ControlText == "mods del juego");
        ModsStatus = $"{Mods.Count} mods, {Mods.Count(m => m.IsEnabled)} activos. Los cambios se aplican al momento. " +
                     (gameFolder
                         ? "Este juego carga los mods desde su carpeta «mods»: se activan y desactivan con su propio archivo disable.it, igual que en el menú de mods del juego."
                         : "Desactivar un mod saca su carpeta de la que lee el juego (no se borra); Steam puede volver a descargarlo mientras sigas suscrito.");
    }

    /// <summary>Activa o desactiva un mod en disco (llamado por la casilla). Null = hecho.</summary>
    private string? ApplyMod(WorkshopMod mod, bool enabled)
    {
        if (_card.IsRunning) { ResultText = "Cierra el juego antes de cambiar sus mods."; return ResultText; }
        var error = WorkshopModManager.SetEnabled(Library!, _card.Entry.AppId, mod, enabled);
        if (error is null)
        {
            Mods.FirstOrDefault(m => m.Id == mod.Id)?.UpdatePath(WorkshopModManager.PathAfter(Library!, _card.Entry.AppId, mod, enabled));
            ResultText = $"«{mod.Title ?? mod.Id.ToString()}» {(enabled ? "activado" : "desactivado")}.";
            // Al terminar de procesar la casilla, refrescar el recuento.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(UpdateModsStatus);
        }
        else
        {
            ResultText = error;
        }
        return error;
    }

    // =====================================================================================

    /// <summary>Aplica los cambios de DLC (los mods ya se aplicaron al marcarlos).</summary>
    private void Apply()
    {
        if (_card.IsRunning) { ResultText = "Cierra el juego antes de cambiar los DLC."; return; }
        if (SteamLocator.IsClientRunning())
        {
            ResultText = "Cierra Steam para cambiar los DLC (lo reescribe al salir) y vuelve a pulsar «Aplicar».";
            return;
        }
        _isBusy = true;
        try
        {
            AppManifestWriter.WriteDisabledDlc(AcfPath, Dlcs.Where(d => d.HasContent && !d.IsEnabled).Select(d => d.Id));
            ResultText = "DLC guardados. Se aplican en la próxima actualización o verificación del juego.";
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResultText = $"No se pudo guardar la configuración de DLC: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>Recarga el estado real tras aplicar (lo marcado pasa a ser el estado inicial).</summary>
    private void Reload()
    {
        var dlcs = Dlcs.Select(d => new DlcItemViewModel(d.Id, d.Name, d.HasContent, d.IsInstalled, d.IsEnabled)).ToList();
        Dlcs.Clear();
        foreach (var d in dlcs) { d.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested(); Dlcs.Add(d); }
        CommandManager.InvalidateRequerySuggested();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"No se pudo abrir {url}: {ex.Message}"); }
    }
}
