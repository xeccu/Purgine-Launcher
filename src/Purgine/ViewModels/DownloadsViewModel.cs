using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.SteamNet;

namespace Purgine.ViewModels;

/// <summary>Acciones del panel de descargas (las implementa la ventana principal).</summary>
public interface IDownloadActions
{
    void PauseDownload(DownloadJob job);
    void ResumeDownload(DownloadJob job);
    void CancelDownload(DownloadJob job);
    void PauseAllDownloads();
    void ResumeAllDownloads();
    void PlayDownloaded(string appId);
    ImageSource? CoverFor(string appId);
}

/// <summary>Textos de estado de una descarga (compartidos por el panel y las tarjetas).</summary>
public static class JobText
{
    public static string Phase(DownloadJob j) => j.State switch
    {
        DownloadJobState.Queued => "En cola",
        DownloadJobState.Preparing => j.Message ?? "Preparando…",
        DownloadJobState.Verifying => "Comprobando archivos en disco",
        DownloadJobState.Downloading => j.Kind == DownloadJobKind.Update ? "Actualizando" : "Descargando",
        DownloadJobState.Paused => j.Message ?? "En pausa",
        DownloadJobState.WaitingForSteam => j.Message ?? "Registrando en Steam…",
        DownloadJobState.Completed => j.Kind == DownloadJobKind.Update ? "Actualizado" : "Instalado",
        DownloadJobState.Failed => j.Message ?? "Error",
        _ => j.Message ?? "Cancelado",
    };

    /// <summary>Línea compacta para la tarjeta de la biblioteca.</summary>
    public static string Short(DownloadJob j)
    {
        var text = Phase(j);
        if (j.Progress is double p && j.State is DownloadJobState.Downloading or DownloadJobState.Paused or DownloadJobState.Verifying)
            text += $" {p * 100:0.0} %";
        if (j.State == DownloadJobState.Downloading && j.BytesPerSecond > 0) text += $" · {Formatting.Speed(j.BytesPerSecond)}";
        if (j.State == DownloadJobState.Downloading && j.Eta is { } eta) text += $" · {Formatting.Eta(eta)}";
        return text;
    }
}

/// <summary>Una descarga en el panel.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private readonly IDownloadActions _actions;

    public DownloadItemViewModel(DownloadJob job, IDownloadActions actions)
    {
        Job = job;
        _actions = actions;
        PauseCommand = new RelayCommand(_ => _actions.PauseDownload(Job), _ => CanPause);
        ResumeCommand = new RelayCommand(_ => _actions.ResumeDownload(Job), _ => CanResume);
        CancelCommand = new RelayCommand(_ => _actions.CancelDownload(Job), _ => !Job.IsFinished && Job.State != DownloadJobState.WaitingForSteam);
    }

    public DownloadJob Job { get; }
    public uint AppId => Job.AppId;
    public string Name => Job.Name;
    public ImageSource? Cover => _actions.CoverFor(Job.AppId.ToString());
    public string KindText => Job.Kind == DownloadJobKind.Update ? "Actualización" : "Instalación";
    public string PhaseText => JobText.Phase(Job);
    public bool IsActive => Job.IsActive;
    public bool IsDownloading => Job.State == DownloadJobState.Downloading;

    public double Percent => (Job.Progress ?? 0) * 100;
    public bool IsIndeterminate => Job.State is DownloadJobState.Preparing || (Job.IsActive && Job.Progress is null);
    public string PercentText => Job.Progress is double p && Job.State is not (DownloadJobState.Queued or DownloadJobState.Preparing)
        ? $"{p * 100:0.0} %" : string.Empty;

    /// <summary>«3,2 GB de 7,0 GB · 12,3 MB/s · 4 min restantes»</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Job.TotalBytes > 0 && Job.State is DownloadJobState.Downloading or DownloadJobState.Paused)
                parts.Add($"{Formatting.Bytes(Job.TransferredBytes)} de {Formatting.Bytes(Job.TotalBytes)}");
            if (IsDownloading && Job.BytesPerSecond > 0) parts.Add(Formatting.Speed(Job.BytesPerSecond));
            if (IsDownloading && Job.Eta is { } eta) parts.Add($"{Formatting.Eta(eta)} restantes");
            return string.Join("  ·  ", parts);
        }
    }

    public bool CanPause => Job.State is DownloadJobState.Queued or DownloadJobState.Preparing or DownloadJobState.Verifying or DownloadJobState.Downloading;
    public bool CanResume => Job.State == DownloadJobState.Paused;

    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>Descarga terminada en esta sesión.</summary>
public sealed class CompletedDownloadViewModel
{
    public CompletedDownloadViewModel(DownloadJob job, IDownloadActions actions)
    {
        AppId = job.AppId.ToString();
        Name = job.Name;
        Completed = job.State == DownloadJobState.Completed;
        StatusText = $"{JobText.Phase(job)} · {DateTime.Now:HH:mm}";
        Cover = actions.CoverFor(AppId);
        PlayCommand = new RelayCommand(_ => actions.PlayDownloaded(AppId), _ => Completed);
    }

    public string AppId { get; }
    public string Name { get; }
    public bool Completed { get; }
    public string StatusText { get; }
    public ImageSource? Cover { get; }
    public ICommand PlayCommand { get; }
}

/// <summary>Pestaña Descargas: cola nativa de Purgine (progreso real, velocidad, tiempo restante, controles).</summary>
public sealed class DownloadsViewModel : ObservableObject
{
    private readonly IDownloadActions _actions;

    public DownloadsViewModel(IDownloadActions actions)
    {
        _actions = actions;
        PauseAllCommand = new RelayCommand(_ => _actions.PauseAllDownloads(), _ => Items.Any(i => i.CanPause));
        ResumeAllCommand = new RelayCommand(_ => _actions.ResumeAllDownloads(), _ => Items.Any(i => i.CanResume));
        ClearCompletedCommand = new RelayCommand(_ => { Completed.Clear(); RaiseCounts(); }, _ => Completed.Count > 0);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
    public ObservableCollection<CompletedDownloadViewModel> Completed { get; } = [];

    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }

    public int BadgeCount => Items.Count(i => !i.Job.IsFinished && i.Job.State != DownloadJobState.Paused);
    public bool HasBadge => BadgeCount > 0;
    public bool IsEmpty => Items.Count == 0 && Completed.Count == 0;
    public bool HasCompleted => Completed.Count > 0;

    public string SummaryText
    {
        get
        {
            var active = Items.FirstOrDefault(i => i.IsDownloading);
            if (active is not null && active.Job.BytesPerSecond > 0)
                return $"{active.Name}: {Formatting.Speed(active.Job.BytesPerSecond)} · sin abrir el cliente de Steam";
            if (Items.Any(i => i.IsActive)) return "Preparando la descarga…";
            if (Items.Count > 0 && Items.All(i => i.Job.State == DownloadJobState.Paused)) return "Descargas en pausa.";
            return Items.Count > 0 ? "En cola." : "No hay descargas.";
        }
    }

    /// <summary>Sincroniza la lista con la cola del gestor (altas y bajas).</summary>
    public void SyncJobs(IReadOnlyList<DownloadJob> jobs)
    {
        foreach (var item in Items.ToList())
            if (!jobs.Contains(item.Job)) Items.Remove(item);
        foreach (var job in jobs)
            if (Items.All(i => i.Job != job)) Items.Add(new DownloadItemViewModel(job, _actions));
        RaiseCounts();
    }

    /// <summary>Refresca una fila (progreso).</summary>
    public void RefreshJob(DownloadJob job)
    {
        Items.FirstOrDefault(i => i.Job == job)?.Refresh();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(HasBadge));
    }

    public void AddFinished(DownloadJob job)
    {
        Completed.Insert(0, new CompletedDownloadViewModel(job, _actions));
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
