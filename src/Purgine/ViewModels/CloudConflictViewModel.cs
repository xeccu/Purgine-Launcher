using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.Core;
using Purgine.Infrastructure;

namespace Purgine.ViewModels;

/// <summary>
/// Diálogo de conflicto de partidas, como el de Steam Cloud: la partida de este equipo frente a la de la nube, con
/// fecha y hora de cada una, y elegir cuál conservar. En los juegos de Steam la elección se hace en la ventana de
/// Steam (es quien sincroniza): aquí se ven los detalles y se decide si seguir.
/// </summary>
public sealed class CloudConflictViewModel : ObservableObject
{
    private readonly TaskCompletionSource<CloudConflictChoice> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImageSource? _banner;

    public CloudConflictViewModel(CloudConflictInfo info)
    {
        Info = info;
        UseCloudCommand = new RelayCommand(_ => Close(CloudConflictChoice.UseCloud));
        KeepLocalCommand = new RelayCommand(_ => Close(CloudConflictChoice.KeepLocal));
        ContinueCommand = new RelayCommand(_ => Close(CloudConflictChoice.Continue));
        CancelCommand = new RelayCommand(_ => Close(CloudConflictChoice.Cancel));
    }

    public CloudConflictInfo Info { get; }
    public Task<CloudConflictChoice> Result => _result.Task;

    public string Name => Info.Game.Name;
    public bool SteamDecides => Info.SteamDecides;
    public bool PurgineDecides => !Info.SteamDecides;

    public ImageSource? Banner
    {
        get => _banner;
        set => SetProperty(ref _banner, value);
    }

    public string Title => "Conflicto de partidas en la nube";

    public string Explanation => Info.SteamDecides
        ? "La partida de este equipo y la de Steam Cloud han cambiado las dos desde la última vez que se sincronizaron. " +
          "Al jugar, Steam te pedirá elegir cuál conservar (Purgine ya ha hecho una copia de seguridad de la local)."
        : Info.AtExit
            ? "Mientras jugabas, otro equipo subió una partida a la nube. ¿Cuál quieres conservar? La que no elijas se " +
              "guarda igualmente: la local en las copias de Purgine y la de la nube sigue en su historial."
            : "La partida de este equipo y la de la nube han cambiado las dos desde la última vez que se sincronizaron. " +
              "¿Cuál quieres cargar? La que no elijas no se pierde: la local queda en las copias de Purgine y la de la nube en su historial.";

    public string LocalWhen => When(Info.LocalTimeUtc);
    public string LocalDetail => $"Este equipo ({Environment.MachineName}) · {Info.LocalFiles} {(Info.LocalFiles == 1 ? "archivo" : "archivos")}";
    public string CloudWhen => When(Info.CloudTimeUtc);
    public string CloudDetail => Info.CloudMachine is { } m ? $"Subida desde «{m}»" : "Última partida subida a la nube";

    /// <summary>La más reciente se marca (como hace Steam).</summary>
    public bool LocalIsNewer => (Info.LocalTimeUtc ?? DateTime.MinValue) >= (Info.CloudTimeUtc ?? DateTime.MinValue);
    public bool CloudIsNewer => !LocalIsNewer;

    public ICommand UseCloudCommand { get; }
    public ICommand KeepLocalCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand CancelCommand { get; }

    private void Close(CloudConflictChoice choice) => _result.TrySetResult(choice);

    private static string When(DateTime? utc)
    {
        if (utc is null) return "Sin fecha";
        var local = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime();
        var es = CultureInfo.GetCultureInfo("es-ES");
        return $"{local.ToString("dddd d 'de' MMMM, HH:mm:ss", es)} · {Formatting.RelativeDate(new DateTimeOffset(utc.Value, TimeSpan.Zero))}";
    }
}
