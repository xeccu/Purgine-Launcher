using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightLauncher.Library;
using LightLauncher.Stats;

namespace LightLauncher.ViewModels;

/// <summary>Una tarjeta del grid de juegos.</summary>
public sealed class GameCardViewModel : ObservableObject
{
    /// <summary>Ancho al que se decodifica la portada: evita tener JPG de 600x900 enteros en RAM.</summary>
    private const int CoverDecodeWidth = 200;

    private readonly GameTimeTracker _tracker;
    private ImageSource? _cover;
    private bool _isRunning;
    private string _playtimeText = string.Empty;

    public GameCardViewModel(GameEntry entry, GameTimeTracker tracker)
    {
        Entry = entry;
        _tracker = tracker;
        RefreshStats();
    }

    public GameEntry Entry { get; }
    public string Name => Entry.Name;
    public string Tooltip => Entry.InstallDir is null
        ? $"{Entry.Name}\nAppID {Entry.AppId}"
        : $"{Entry.Name}\nAppID {Entry.AppId}\n{Entry.InstallDir}";

    public ImageSource? Cover
    {
        get => _cover;
        private set { if (SetProperty(ref _cover, value)) OnPropertyChanged(nameof(HasCover)); }
    }

    public bool HasCover => _cover is not null;

    public bool IsRunning
    {
        get => _isRunning;
        set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(OverlayText)); }
    }

    public string OverlayText => _isRunning ? "En curso" : (Entry.IsFullyInstalled ? "Jugar" : "Actualizar y jugar");

    public string PlaytimeText
    {
        get => _playtimeText;
        private set => SetProperty(ref _playtimeText, value);
    }

    public void RefreshStats()
    {
        var total = _tracker.GetTotal(Entry.Id);
        var last = _tracker.GetLastPlayed(Entry.Id);
        PlaytimeText = total == TimeSpan.Zero
            ? "Sin jugar"
            : $"{Formatting.Duration(total)} · {Formatting.RelativeDate(last)}";
    }

    /// <summary>Carga la portada en segundo plano (decodificada a tamaño reducido y congelada).</summary>
    public async Task LoadCoverAsync()
    {
        var path = Entry.CoverImagePath;
        if (path is null || _cover is not null) return;
        Cover = await Task.Run(() => LoadBitmap(path));
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // lee todo y suelta el archivo
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = CoverDecodeWidth;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();                                  // usable desde el hilo de UI
            return bmp;
        }
        catch (Exception)
        {
            return null; // imagen corrupta o en uso: se muestra el placeholder
        }
    }
}
