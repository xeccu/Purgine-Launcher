using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LightLauncher.Core;

namespace LightLauncher.ViewModels;

/// <summary>Acciones del panel de descargas (las implementa la ventana principal).</summary>
public interface IDownloadActions
{
    bool IsSteamPaused { get; }
    void PauseDownloads();
    void ResumeDownloads();
    void CancelDownload(DownloadItemViewModel item);
    void PlayDownloaded(string appId);
    ImageSource? CoverFor(string appId);
}

/// <summary>Una descarga en curso o en cola.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private readonly IDownloadActions _actions;
    private DownloadInfo _info;

    public DownloadItemViewModel(DownloadInfo info, IDownloadActions actions)
    {
        _info = info;
        _actions = actions;
        CancelCommand = new RelayCommand(_ => _actions.CancelDownload(this), _ => CanCancel);
    }

    public DownloadInfo Info => _info;
    public string AppId => _info.AppId;
    public string Name => _info.Name;
    public ImageSource? Cover => _actions.CoverFor(_info.AppId);
    public string PhaseText => _info.PhaseText;
    public bool IsActive => _info.IsActive;
    public bool IsDownloading => _info.Phase == DownloadPhase.Downloading;

    public double Percent => (_info.Progress ?? 0) * 100;
    /// <summary>Barra animada solo cuando Steam trabaja sin un porcentaje medible (no para lo que está en espera).</summary>
    public bool IsIndeterminate => _info.IsActive && (_info.Progress is null || _info.Phase is DownloadPhase.Preparing
        or DownloadPhase.Preallocating or DownloadPhase.Verifying or DownloadPhase.Committing or DownloadPhase.Uninstalling);
    /// <summary>Porcentaje solo cuando hay una descarga en marcha o en pausa (no en lo que espera).</summary>
    public string PercentText =>
        _info.Progress is double p && (_info.IsActive || _info.Phase == DownloadPhase.Paused) ? $"{p * 100:0.0} %" : string.Empty;

    /// <summary>«3,2 GB de 7,0 GB · 12,3 MB/s · 4 min restantes»</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (_info.BytesTotal > 0 && _info.Phase == DownloadPhase.Queued)
                parts.Add($"{Formatting.Bytes(_info.BytesTotal)} por descargar");
            else if (_info.BytesTotal > 0)
                parts.Add($"{Formatting.Bytes(_info.BytesDownloaded)} de {Formatting.Bytes(_info.BytesTotal)}");
            if (IsDownloading && _info.BytesPerSecond > 0) parts.Add(Formatting.Speed(_info.BytesPerSecond));
            if (IsDownloading && _info.Eta is { } eta) parts.Add($"{Formatting.Eta(eta)} restantes");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Se puede cancelar lo pedido desde LightLauncher o lo que Steam está descargando. Una actualización
    /// pendiente en reposo no: la única forma silenciosa de cancelarla sería desinstalar el juego.
    /// </summary>
    public bool CanCancel => _info.Phase is not (DownloadPhase.Uninstalling or DownloadPhase.Stopping)
                             && (_info.IsRequested || _info.IsActive || _info.Phase == DownloadPhase.Paused);

    public ICommand CancelCommand { get; }

    public void Update(DownloadInfo info)
    {
        _info = info;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Descarga terminada en esta sesión.</summary>
public sealed class CompletedDownloadViewModel
{
    public CompletedDownloadViewModel(DownloadInfo info, IDownloadActions actions)
    {
        AppId = info.AppId;
        Name = info.Name;
        Completed = info.Phase == DownloadPhase.Completed;
        StatusText = info.Phase switch
        {
            DownloadPhase.Completed => $"Instalado · {DateTime.Now:HH:mm}",
            DownloadPhase.Canceled => $"Cancelado · {DateTime.Now:HH:mm}",
            _ => $"Desinstalado · {DateTime.Now:HH:mm}",
        };
        Cover = actions.CoverFor(info.AppId);
        PlayCommand = new RelayCommand(_ => actions.PlayDownloaded(AppId), _ => Completed);
    }

    public string AppId { get; }
    public string Name { get; }
    public bool Completed { get; }
    public string StatusText { get; }
    public ImageSource? Cover { get; }
    public ICommand PlayCommand { get; }
}

/// <summary>Pestaña Descargas: cola en vivo con progreso real, velocidad, tiempo restante y controles.</summary>
public sealed class DownloadsViewModel : ObservableObject
{
    private readonly IDownloadActions _actions;
    private bool _isPaused;

    public DownloadsViewModel(IDownloadActions actions)
    {
        _actions = actions;
        PauseCommand = new RelayCommand(_ => _actions.PauseDownloads(), _ => !IsPaused && Items.Any(i => i.IsActive));
        ResumeCommand = new RelayCommand(_ => _actions.ResumeDownloads(), _ => IsPaused);
        ClearCompletedCommand = new RelayCommand(_ => { Completed.Clear(); RaiseCounts(); }, _ => Completed.Count > 0);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
    public ObservableCollection<CompletedDownloadViewModel> Completed { get; } = [];

    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand ClearCompletedCommand { get; }

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    /// <summary>Número para la insignia de la pestaña (descargas activas o pedidas).</summary>
    public int BadgeCount => Items.Count(i => i.IsActive || i.Info.IsRequested);
    public bool HasBadge => BadgeCount > 0;
    public bool IsEmpty => Items.Count == 0 && Completed.Count == 0;
    public bool HasCompleted => Completed.Count > 0;

    public string SummaryText
    {
        get
        {
            var active = Items.FirstOrDefault(i => i.IsDownloading);
            if (IsPaused) return "Descargas en pausa: Steam está congelado hasta que pulses «Reanudar».";
            if (active is not null && active.Info.BytesPerSecond > 0)
                return $"Descargando {active.Name} a {Formatting.Speed(active.Info.BytesPerSecond)}";
            if (Items.Any(i => i.IsActive)) return "Steam está trabajando en tus descargas.";
            return Items.Count > 0 ? "Nada descargándose ahora mismo." : "No hay descargas.";
        }
    }

    public void Apply(IReadOnlyList<DownloadInfo> list)
    {
        IsPaused = _actions.IsSteamPaused;
        var byId = list.ToDictionary(d => d.AppId);

        foreach (var item in Items.ToList())
        {
            if (byId.Remove(item.AppId, out var info)) item.Update(info);
            else Items.Remove(item);
        }
        foreach (var info in byId.Values) Items.Add(new DownloadItemViewModel(info, _actions));

        // Orden: activas primero (igual que el monitor).
        var ordered = list.Select(d => Items.First(i => i.AppId == d.AppId)).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var current = Items.IndexOf(ordered[i]);
            if (current != i) Items.Move(current, i);
        }
        RaiseCounts();
    }

    public void AddFinished(DownloadInfo info)
    {
        Completed.Insert(0, new CompletedDownloadViewModel(info, _actions));
        while (Completed.Count > 20) Completed.RemoveAt(Completed.Count - 1);
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(HasBadge));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(SummaryText));
        CommandManager.InvalidateRequerySuggested();
    }
}
