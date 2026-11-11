using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.SteamNet;

namespace Purgine.ViewModels;

/// <summary>Un contrato del diálogo, con su enlace para leerlo.</summary>
public sealed class EulaItemViewModel(SteamEula eula)
{
    public string Name => eula.Name;
    public string VersionText => eula.Version > 0 ? $"Versión {eula.Version}" : string.Empty;
    public ICommand OpenCommand { get; } = new RelayCommand(_ =>
    {
        try { Process.Start(new ProcessStartInfo(eula.Url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Infrastructure.Log.Warn($"No se pudo abrir el contrato {eula.Url}: {ex.Message}"); }
    });
}

/// <summary>
/// Contrato de licencia de un juego de Steam pendiente de aceptar, dentro de Purgine (sin abrir Steam): nombre de
/// cada contrato con enlace para leerlo, y «Aceptar licencia y jugar» / «Cancelar».
/// </summary>
public sealed class EulaDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImageSource? _banner;

    public EulaDialogViewModel(string gameName, IReadOnlyList<SteamEula> eulas, bool steamWillRestart)
    {
        Name = gameName;
        Eulas = eulas.Select(e => new EulaItemViewModel(e)).ToList();
        Explanation = (eulas.Count == 1
                ? $"Para jugar a {gameName} hay que aceptar su contrato de licencia de usuario final."
                : $"Para jugar a {gameName} hay que aceptar sus {eulas.Count} contratos de licencia.")
            + " Puedes leerlo antes en la página de Steam. Al aceptar, Purgine lo anota en tu Steam igual que haría el propio"
            + " cliente, y el juego arranca sin abrir la ventana de Steam."
            + (steamWillRestart ? " Steam está abierto: se cerrará un momento para registrarlo y se volverá a abrir en segundo plano." : string.Empty);
        AcceptCommand = new RelayCommand(_ => _result.TrySetResult(true));
        CancelCommand = new RelayCommand(_ => _result.TrySetResult(false));
    }

    public string Name { get; }
    public string Explanation { get; }
    public IReadOnlyList<EulaItemViewModel> Eulas { get; }
    public Task<bool> Result => _result.Task;

    public ImageSource? Banner
    {
        get => _banner;
        set => SetProperty(ref _banner, value);
    }

    public ICommand AcceptCommand { get; }
    public ICommand CancelCommand { get; }
}
