using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Purgine.Core;
using Purgine.Library;

namespace Purgine.ViewModels;

/// <summary>Ficha: estado de las partidas en la nube y carpeta de partidas del juego.</summary>
public sealed partial class GameDetailViewModel
{
    private CloudSaveManager? _cloud;
    private CloudSaveStatus _cloudStatus = new(CloudSaveState.Unknown, "Sin comprobar");

    private void InitCloud(CloudSaveManager? cloud)
    {
        _cloud = cloud;
        // En Steam la carpeta la decide Steam Cloud: no se elige a mano.
        ChooseSaveFolderCommand = new RelayCommand(_ => ChooseSaveFolder(), _ => _cloud is not null && !Card.IsSteam);
        ResetSaveFolderCommand = new RelayCommand(_ => ResetSaveFolder(),
            _ => !Card.IsSteam && _cloud?.Paths.GetManual(Card.Entry.Id) is not null);
        OpenSaveFolderCommand = new RelayCommand(_ => OpenFolder(_cloudStatus.SavePath),
            _ => _cloudStatus.SavePath is { } p && Directory.Exists(p));
        OpenSaveBackupsCommand = new RelayCommand(_ => OpenFolder(_cloud?.BackupFolderFor(Card.Entry)),
            _ => _cloud is not null && Directory.Exists(_cloud.BackupFolderFor(Card.Entry)));
        if (_cloud is null) return;
        _cloudStatus = _cloud.GetStatus(Card.Entry.Id);
        _cloud.StatusChanged += OnCloudStatus;
    }

    private void CloseCloud()
    {
        if (_cloud is not null) _cloud.StatusChanged -= OnCloudStatus;
    }

    private void OnCloudStatus(string gameId, CloudSaveStatus status)
    {
        if (gameId != Card.Entry.Id) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _cloudStatus = status;
            RaiseCloud();
        });
    }

    private void RaiseCloud()
    {
        OnPropertyChanged(nameof(CloudText));
        OnPropertyChanged(nameof(CloudTitle));
        OnPropertyChanged(nameof(CloudGlyph));
        OnPropertyChanged(nameof(CloudTooltip));
        OnPropertyChanged(nameof(IsCloudBusy));
        OnPropertyChanged(nameof(IsCloudOk));
        OnPropertyChanged(nameof(IsCloudError));
        OnPropertyChanged(nameof(IsCloudWarning));
        CommandManager.InvalidateRequerySuggested();
    }

    private Task LoadCloudAsync(CancellationToken ct) =>
        ShowCloud ? Task.Run(() => _cloud!.RefreshStatusAsync(Card.Entry, ct), ct) : Task.CompletedTask;

    /// <summary>Indicador visible: juegos de Epic y de Steam instalados.</summary>
    public bool ShowCloud => _cloud is not null && (Card.IsEpic || Card.HasValidAppId) && !Card.IsNotInstalled;

    /// <summary>«Nube sincronizada», «Sincronizando…», «Guardado local»…</summary>
    public string CloudTitle => _cloudStatus.State switch
    {
        CloudSaveState.Checking => "Comprobando…",
        CloudSaveState.Downloading or CloudSaveState.Uploading => "Sincronizando…",
        CloudSaveState.Synced => Card.IsSteam ? "Steam Cloud al día" : "Nube sincronizada",
        CloudSaveState.LocalOnly => Card.IsSteam ? "Cambios sin subir" : "Guardado local",
        CloudSaveState.RemoteNewer => "Nube más reciente",
        CloudSaveState.Conflict => "Conflicto",
        CloudSaveState.NotManaged => "Lo gestiona el juego",
        CloudSaveState.Error => "Sin conexión",
        _ => "—",
    };

    public string CloudText => _cloudStatus.Text;

    /// <summary>Icono (Segoe MDL2): nube con check, sincronizar, disco, aviso.</summary>
    public string CloudGlyph => _cloudStatus.State switch
    {
        CloudSaveState.Checking or CloudSaveState.Downloading or CloudSaveState.Uploading => "",
        CloudSaveState.Synced => "",
        CloudSaveState.LocalOnly => "",
        CloudSaveState.Error or CloudSaveState.Conflict => "",
        CloudSaveState.RemoteNewer => "",
        _ => "",
    };

    public bool IsCloudBusy => _cloudStatus.State is CloudSaveState.Checking or CloudSaveState.Downloading or CloudSaveState.Uploading;
    public bool IsCloudOk => _cloudStatus.State == CloudSaveState.Synced;
    public bool IsCloudError => _cloudStatus.State == CloudSaveState.Error;
    public bool IsCloudWarning => _cloudStatus.State is CloudSaveState.Conflict or CloudSaveState.RemoteNewer;

    public string CloudTooltip
    {
        get
        {
            var text = _cloudStatus.Text;
            if (_cloudStatus.SavePath is { } path)
            {
                var source = Card.IsSteam ? "según Steam Cloud" : _cloudStatus.Source switch
                {
                    SavePathSource.Manual => "elegida por ti",
                    SavePathSource.EpicCatalog => "según Epic",
                    _ => "deducida, compruébala",
                };
                text += $"\nPartidas: {path} ({source})";
            }
            return text + "\nClic: opciones de la carpeta de partidas y copias de seguridad.";
        }
    }

    public ICommand ChooseSaveFolderCommand { get; private set; } = null!;
    public ICommand ResetSaveFolderCommand { get; private set; } = null!;
    public ICommand OpenSaveFolderCommand { get; private set; } = null!;
    public ICommand OpenSaveBackupsCommand { get; private set; } = null!;

    private void ChooseSaveFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Carpeta de partidas guardadas de {Card.Name}",
        };
        if (_cloudStatus.SavePath is { } current && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog() != true) return;
        _cloud!.Paths.SetManual(Card.Entry.Id, dialog.FolderName);
        _ = LoadCloudAsync(_cts.Token);
    }

    private void ResetSaveFolder()
    {
        _cloud!.Paths.SetManual(Card.Entry.Id, null);
        _ = LoadCloudAsync(_cts.Token);
    }

    private static void OpenFolder(string? path)
    {
        Infrastructure.ShellOpen.Folder(path);
    }
}
