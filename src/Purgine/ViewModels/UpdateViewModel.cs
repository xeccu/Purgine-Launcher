using System.Text.RegularExpressions;
using System.Windows.Input;
using Purgine.Infrastructure;
using Purgine.Settings;
using Purgine.Updates;

namespace Purgine.ViewModels;

/// <summary>
/// Aviso de nueva versión (superposición) y el apartado «Actualizaciones» de Ajustes. Al iniciar se consulta GitHub
/// Releases en segundo plano; si hay una versión mayor, se muestra el aviso con sus notas. «Actualizar ahora» descarga
/// el instalador con progreso y pide a la ventana que cierre Purgine y lo lance en modo <c>--update</c>.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly LauncherSettings _settings;
    private readonly Action _save;
    private readonly UpdateService _service = new();
    private CancellationTokenSource? _downloadCts;
    private ReleaseInfo? _release;
    private bool _isOpen;
    private bool _isDownloading;
    private bool _isChecking;
    private double _progress;
    private string _progressText = string.Empty;
    private string? _error;
    private string _statusText = string.Empty;

    public UpdateViewModel(LauncherSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        UpdateNowCommand = new RelayCommand(async _ => await DownloadAndInstallAsync(), _ => _release is not null && !_isDownloading);
        LaterCommand = new RelayCommand(_ => IsOpen = false, _ => !_isDownloading);
        SkipCommand = new RelayCommand(_ => Skip(), _ => _release is not null && !_isDownloading);
        CancelDownloadCommand = new RelayCommand(_ => _downloadCts?.Cancel(), _ => _isDownloading);
        CheckNowCommand = new RelayCommand(async _ => await CheckAsync(manual: true), _ => !_isChecking && !_isDownloading);
        OpenReleasesCommand = new RelayCommand(_ => ShellOpen.Url(_release?.PageUrl ?? UpdateService.ReleasesPage));
    }

    /// <summary>La ventana cierra Purgine (guardando todo) y abre el instalador descargado.</summary>
    public event EventHandler<string>? InstallRequested;

    public ICommand UpdateNowCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand CheckNowCommand { get; }
    public ICommand OpenReleasesCommand { get; }

    public string CurrentVersion => AppVersion.Display;

    public bool CheckOnStartup
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value) return;
            _settings.CheckForUpdates = value;
            _save();
            OnPropertyChanged();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set { if (SetProperty(ref _isDownloading, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Resultado de la última comprobación (Ajustes → Actualizaciones).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string NewVersion => _release is null ? string.Empty : _release.Version.ToString();
    public string Title => _release?.Title ?? string.Empty;
    public string PublishedText => _release?.PublishedAt is { } at ? $"Publicada el {at.LocalDateTime:d 'de' MMMM 'de' yyyy}" : string.Empty;
    public string SizeText => _release is null ? string.Empty : $"{_release.Installer.Size / 1048576.0:0.#} MB";
    public string Notes => _release is null ? string.Empty : CleanMarkdown(_release.Notes);
    public bool HasNotes => Notes.Length > 0;

    /// <summary>Comprobación automática al arrancar (en segundo plano, sin molestar si falla o no hay nada).</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_settings.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(6));
        await CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (_isChecking || _isDownloading) return;
        _isChecking = true;
        CommandManager.InvalidateRequerySuggested();
        StatusText = "Buscando actualizaciones…";
        try
        {
            var release = await _service.CheckAsync(CancellationToken.None);
            if (release is null)
            {
                StatusText = $"Tienes la última versión ({AppVersion.Display}).";
                return;
            }
            StatusText = $"Hay una versión nueva: {release.Version}.";
            if (!manual && string.Equals(_settings.SkippedUpdateVersion, release.Version.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Actualización {release.Tag} omitida por el usuario.");
                return;
            }
            _release = release;
            Error = null;
            OnPropertyChanged(string.Empty);
            IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo comprobar si hay actualizaciones: {ex.Message}");
            StatusText = manual ? $"No se pudo comprobar: {ex.Message}" : string.Empty;
        }
        finally
        {
            _isChecking = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Skip()
    {
        _settings.SkippedUpdateVersion = _release?.Version.ToString();
        _save();
        IsOpen = false;
    }

    private async Task DownloadAndInstallAsync()
    {
        if (_release is null) return;
        Error = null;
        IsDownloading = true;
        Progress = 0;
        ProgressText = "Conectando con GitHub…";
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                Progress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                ProgressText = $"Descargando {p.Done / 1048576.0:0.0} de {p.Total / 1048576.0:0.0} MB";
            });
            var path = await _service.DownloadAsync(_release, progress, _downloadCts.Token);
            ProgressText = "Instalando… Purgine se cerrará y se volverá a abrir actualizado.";
            await Task.Delay(400);
            InstallRequested?.Invoke(this, path);
        }
        catch (OperationCanceledException)
        {
            ProgressText = string.Empty;
            IsDownloading = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Fallo al descargar la actualización: {ex.Message}");
            Error = $"No se pudo descargar la actualización: {ex.Message}";
            IsDownloading = false;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    /// <summary>La ventana no pudo cerrarse (p. ej. el usuario canceló por una partida en curso).</summary>
    public void InstallCancelled()
    {
        IsDownloading = false;
        ProgressText = string.Empty;
        Error = "Actualización pospuesta: Purgine sigue abierto. Pulsa «Actualizar ahora» cuando quieras.";
    }

    /// <summary>Notas de la Release en texto plano: sin almohadillas de títulos, negritas ni enlaces markdown.</summary>
    private static string CleanMarkdown(string md)
    {
        var s = md.Replace("\r\n", "\n");
        s = HeaderRegex().Replace(s, string.Empty);
        s = s.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        s = LinkRegex().Replace(s, "$1");
        s = BulletRegex().Replace(s, "• ");
        return s.Trim();
    }

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?m)^\s*[-*]\s+")]
    private static partial Regex BulletRegex();

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _service.Dispose();
    }
}
