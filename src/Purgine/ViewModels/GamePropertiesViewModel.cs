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
        : this(id.ToString(), name, hasContent, isInstalled, isEnabled) => Id = id;

    /// <summary>Opción de contenido de Epic (clave = id de la opción en el SDMeta de Epic).</summary>
    public DlcItemViewModel(string key, string name, bool hasContent, bool isInstalled, bool isEnabled, string? sizeText = null, string? description = null)
    {
        Key = key;
        SizeText = sizeText;
        Description = description;
        Name = name;
        HasContent = hasContent;
        IsInstalled = isInstalled;
        _isEnabled = isEnabled;
        InitialEnabled = isEnabled;
    }

    public uint Id { get; }
    /// <summary>Id del DLC (Steam) o de la opción de Epic.</summary>
    public string Key { get; }
    /// <summary>«+27,4 GB» (Epic), o null.</summary>
    public string? SizeText { get; }
    /// <summary>Descripción de Epic, o null.</summary>
    public string? Description { get; }
    public string Name { get; }
    /// <summary>Tiene archivos propios (depot): se puede activar o desactivar.</summary>
    public bool HasContent { get; }
    public bool IsInstalled { get; }
    public bool InitialEnabled { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (SetProperty(ref _isEnabled, value)) OnPropertyChanged(nameof(Detail)); }
    }

    public string Detail =>
        !HasContent ? "Sin archivos propios: lo activa tu licencia (no se puede desactivar)"
        : IsInstalled ? (IsEnabled ? "Instalado" : "Instalado · se borrará al aplicar")
        : IsEnabled && !InitialEnabled ? "No instalado · se descargará al aplicar"
        : "No instalado";
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
/// Propiedades de un juego instalado: DLC (instalar o quitar de verdad los que tienen archivos propios: «Aplicar»
/// pone en Descargas una actualización que baja los activados y borra del disco los desactivados) y mods de Workshop
/// (activar/desactivar moviendo su carpeta, al momento).
/// </summary>
public sealed class GamePropertiesViewModel : ObservableObject
{
    private readonly GameCardViewModel _card;
    private readonly SteamSession _session;
    private readonly SteamOwnershipService _ownership;
    private readonly SteamCommunityService _community;
    private readonly Func<IReadOnlyCollection<uint>, string?>? _applyDlc;
    private readonly Func<Task<(IReadOnlyList<DlcItemViewModel> Items, string Status)>>? _loadEpic;
    private readonly Func<IReadOnlyCollection<string>, string?>? _applyEpic;
    private bool _isDlcTab = true;
    private string _dlcStatus = "Cargando los DLC…";
    private string _modsStatus = "Buscando mods…";
    private string _resultText = string.Empty;
    private bool _isBusy;

    /// <param name="applyDlc">
    /// Pone en Descargas la actualización con estos DLC desactivados (los demás se instalan). Devuelve el error o null.
    /// </param>
    public GamePropertiesViewModel(GameCardViewModel card, SteamSession session, SteamOwnershipService ownership, SteamCommunityService community,
        Func<IReadOnlyCollection<uint>, string?>? applyDlc = null)
    {
        _applyDlc = applyDlc;
        _card = card;
        _session = session;
        _ownership = ownership;
        _community = community;
        ApplyCommand = new RelayCommand(_ => Apply(), _ => !_isBusy && HasChanges);
        OpenWorkshopCommand = new RelayCommand(_ => OpenUrl($"https://steamcommunity.com/app/{card.Entry.AppId}/workshop/"));
        OpenSubscriptionsCommand = new RelayCommand(_ => OpenUrl(
            $"https://steamcommunity.com/my/myworkshopfiles/?appid={card.Entry.AppId}&browsefilter=mysubscriptions"));
    }

    /// <summary>
    /// Propiedades de un juego de Epic instalado por Purgine: las opciones de contenido de Epic (las mismas que en
    /// Epic Games Launcher) con su estado real en disco. «Aplicar» descarga las marcadas y borra las desmarcadas.
    /// </summary>
    public static GamePropertiesViewModel ForEpic(GameCardViewModel card, SteamSession session, SteamOwnershipService ownership,
        SteamCommunityService community, Func<Task<(IReadOnlyList<DlcItemViewModel> Items, string Status)>> load,
        Func<IReadOnlyCollection<string>, string?> apply) =>
        new(card, session, ownership, community, null, load, apply);

    private GamePropertiesViewModel(GameCardViewModel card, SteamSession session, SteamOwnershipService ownership, SteamCommunityService community,
        Func<IReadOnlyCollection<uint>, string?>? applyDlc, Func<Task<(IReadOnlyList<DlcItemViewModel> Items, string Status)>> loadEpic,
        Func<IReadOnlyCollection<string>, string?> applyEpic)
        : this(card, session, ownership, community, applyDlc)
    {
        _loadEpic = loadEpic;
        _applyEpic = applyEpic;
        _dlcStatus = "Consultando las opciones de Epic…";
    }

    public bool IsEpic => _loadEpic is not null;
    /// <summary>La pestaña de Workshop solo existe en Steam.</summary>
    public bool ShowModsTab => !IsEpic;
    public string ApplyText => IsEpic ? "Aplicar" : "Aplicar DLC";

    public string Title => $"Propiedades — {_card.Name}";
    public string GameName => _card.Name;

    public bool IsDlcTab { get => _isDlcTab; set { if (SetProperty(ref _isDlcTab, value)) OnPropertyChanged(nameof(IsModsTab)); } }
    public bool IsModsTab { get => !_isDlcTab; set => IsDlcTab = !value; }

    public ObservableCollection<DlcItemViewModel> Dlcs { get; } = [];
    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    public string DlcStatus { get => _dlcStatus; private set => SetProperty(ref _dlcStatus, value); }
    public string ModsStatus { get => _modsStatus; private set => SetProperty(ref _modsStatus, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public string DlcTabHeader => IsEpic ? $"Contenido opcional ({Dlcs.Count})" : $"DLC ({Dlcs.Count})";
    public string ModsTabHeader => $"Workshop ({Mods.Count})";

    /// <summary>Solo los DLC esperan a «Aplicar» (necesitan Steam cerrado); los mods se aplican al momento.</summary>
    /// También cuenta un DLC cuyo estado en disco no coincide con lo elegido (desactivado pero aún instalado, o activado
    /// sin instalar): «Aplicar» lo pone en orden.
    public bool HasChanges => Dlcs.Any(d => d.HasContent && (d.IsEnabled != d.InitialEnabled || d.IsEnabled != d.IsInstalled));

    public ICommand ApplyCommand { get; }
    public ICommand OpenWorkshopCommand { get; }
    public ICommand OpenSubscriptionsCommand { get; }

    private string? Library => _card.Entry.LibraryPath;
    private string AcfPath => Path.Combine(Library!, "steamapps", $"appmanifest_{_card.Entry.AppId}.acf");

    // =====================================================================================

    public async Task LoadAsync()
    {
        if (IsEpic) { await LoadEpicAsync(); return; }
        await Task.WhenAll(LoadDlcsAsync(), LoadModsAsync());
    }

    private async Task LoadEpicAsync()
    {
        try
        {
            var (items, status) = await _loadEpic!();
            foreach (var item in items)
            {
                item.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
                Dlcs.Add(item);
            }
            DlcStatus = status;
        }
        catch (Exception ex)
        {
            Log.Warn($"Propiedades (Epic) de {_card.Name}: {ex.Message}");
            DlcStatus = $"No se pudieron consultar las opciones en Epic: {ex.Message}";
        }
        OnPropertyChanged(nameof(DlcTabHeader));
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
        var depotToDlc = info["depots"].Children
            .Where(d => uint.TryParse(d.Name, out _) && d["dlcappid"].AsUnsignedInteger() != 0)
            .ToDictionary(d => uint.Parse(d.Name!), d => d["dlcappid"].AsUnsignedInteger());

        var installed = InstalledDlcDepots(depotToDlc);
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
              " Al aplicar, Purgine descarga los DLC que actives y borra del disco los que desactives (con Steam cerrado).";
    }

    /// <summary>
    /// DLC con depot instalado según el appmanifest (InstalledDepots: por su <c>dlcappid</c> o, si falta, por el depot
    /// según Steam) y cuya carpeta del juego existe.
    /// </summary>
    private HashSet<uint> InstalledDlcDepots(IReadOnlyDictionary<uint, uint> depotToDlc)
    {
        try
        {
            if (_card.Entry.InstallDir is not { } dir || !Directory.Exists(dir)) return [];
            var depots = VdfParser.ParseFile(AcfPath)["AppState"]?["InstalledDepots"];
            if (depots is null) return [];
            var result = new HashSet<uint>();
            foreach (var (name, node) in depots.Children)
            {
                if (uint.TryParse(node.GetValue("dlcappid"), out var id) && id != 0) result.Add(id);
                else if (uint.TryParse(name, out var depot) && depotToDlc.TryGetValue(depot, out var dlc)) result.Add(dlc);
            }
            return result;
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
        if (_card.IsRunning) { ResultText = "Cierra el juego antes de cambiar su contenido."; return; }
        if (IsEpic) { ApplyEpic(); return; }
        if (SteamLocator.IsClientRunning())
        {
            ResultText = "Cierra Steam para cambiar los DLC (lo reescribe al salir) y vuelve a pulsar «Aplicar».";
            return;
        }
        _isBusy = true;
        try
        {
            var disabled = Dlcs.Where(d => d.HasContent && !d.IsEnabled).Select(d => d.Id).ToList();
            var added = Dlcs.Count(d => d.HasContent && d.IsEnabled && !d.IsInstalled);
            var removed = Dlcs.Count(d => d.HasContent && !d.IsEnabled && d.IsInstalled);
            if (_applyDlc is not null)
            {
                // Una actualización de Purgine: baja solo los depots de los DLC activados y borra los de los desactivados.
                if (_applyDlc(disabled) is { } error) { ResultText = error; return; }
                ResultText = added == 0 && removed == 0
                    ? "Guardado. No hay nada que descargar ni borrar."
                    : $"En Descargas: {(added > 0 ? $"se descargará{(added > 1 ? "n" : "")} {added} DLC" : "")}" +
                      $"{(added > 0 && removed > 0 ? " y " : "")}{(removed > 0 ? $"se borrará{(removed > 1 ? "n" : "")} {removed} del disco" : "")}.";
            }
            else
            {
                AppManifestWriter.WriteDisabledDlc(AcfPath, disabled);
                ResultText = "DLC guardados. Se aplican en la próxima actualización del juego.";
            }
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

    /// <summary>Epic: pone en Descargas la actualización con estas opciones (baja lo marcado y borra lo desmarcado).</summary>
    private void ApplyEpic()
    {
        var added = Dlcs.Count(d => d.IsEnabled && !d.IsInstalled);
        var removed = Dlcs.Count(d => !d.IsEnabled && d.IsInstalled);
        if (_applyEpic!(Dlcs.Where(d => d.IsEnabled).Select(d => d.Key).ToList()) is { } error) { ResultText = error; return; }
        ResultText = $"En Descargas: {(added > 0 ? $"se descargará{(added > 1 ? "n" : "")} {added}" : "")}" +
                     $"{(added > 0 && removed > 0 ? " y " : "")}{(removed > 0 ? $"se borrará{(removed > 1 ? "n" : "")} {removed} del disco" : "")}" +
                     (added == 0 && removed == 0 ? "se comprobará el contenido" : "") + ".";
        var items = Dlcs.Select(d => new DlcItemViewModel(d.Key, d.Name, true, d.IsInstalled, d.IsEnabled, d.SizeText, d.Description)).ToList();
        Dlcs.Clear();
        foreach (var d in items) { d.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested(); Dlcs.Add(d); }
        CommandManager.InvalidateRequerySuggested();
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
