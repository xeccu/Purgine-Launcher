using SteamKit2.Authentication;

namespace Purgine.SteamNet;

/// <summary>
/// Puente entre Steam Guard (SteamKit2) y la interfaz de Purgine. SteamKit2 llama a estos métodos desde
/// un hilo de fondo; los delegados se encargan de mostrar los diálogos en el hilo de UI.
/// </summary>
public sealed class GuardAuthenticator : IAuthenticator
{
    private readonly Func<string, string, bool, Task<string?>> _askCode;
    private readonly Func<Task<bool>> _deviceConfirmation;

    /// <param name="askCode">(título, mensaje, código anterior incorrecto) → código o null si se cancela.</param>
    /// <param name="deviceConfirmation">Avisa de que hay que aprobar en la app Steam Mobile; true = esperar la aprobación.</param>
    public GuardAuthenticator(Func<string, string, bool, Task<string?>> askCode, Func<Task<bool>> deviceConfirmation)
    {
        _askCode = askCode;
        _deviceConfirmation = deviceConfirmation;
    }

    public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        var code = await _askCode("Steam Guard",
            "Introduce el código de la app Steam Mobile (Steam Guard).", previousCodeWasIncorrect).ConfigureAwait(false);
        return code ?? throw new OperationCanceledException("Inicio de sesión cancelado.");
    }

    public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        var code = await _askCode("Steam Guard",
            $"Steam ha enviado un código a tu correo ({email}). Introdúcelo aquí.", previousCodeWasIncorrect).ConfigureAwait(false);
        return code ?? throw new OperationCanceledException("Inicio de sesión cancelado.");
    }

    public Task<bool> AcceptDeviceConfirmationAsync() => _deviceConfirmation();
}
