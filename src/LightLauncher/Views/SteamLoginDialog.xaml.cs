using System.Windows;
using LightLauncher.Infrastructure;
using LightLauncher.SteamNet;
using SteamKit2;
using SteamKit2.Authentication;

namespace LightLauncher.Views;

/// <summary>
/// Inicio de sesión en Steam con SteamKit2: usuario + contraseña y Steam Guard (aprobación en la app móvil,
/// código de la app o código por correo). La contraseña solo vive en memoria durante la petición.
/// </summary>
public partial class SteamLoginDialog : Window
{
    private readonly SteamSession _session;
    private CancellationTokenSource? _cts;

    private SteamLoginDialog(SteamSession session)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        _session = session;
        UserBox.Text = session.SavedAccountName ?? string.Empty;
        Loaded += (_, _) => { if (UserBox.Text.Length > 0) PasswordBox.Focus(); else UserBox.Focus(); };
        Closing += (_, _) => _cts?.Cancel();
    }

    /// <summary>Muestra el diálogo. True si la sesión quedó iniciada.</summary>
    public static bool Show(Window? owner, SteamSession session) =>
        new SteamLoginDialog(session) { Owner = owner }.ShowDialog() == true;

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        var password = PasswordBox.Password;
        if (user.Length == 0 || password.Length == 0)
        {
            ShowError("Escribe tu nombre de cuenta y tu contraseña.");
            return;
        }

        SetBusy(true, "Conectando con Steam…");
        _cts = new CancellationTokenSource();
        var authenticator = new GuardAuthenticator(AskCodeAsync, DeviceConfirmationAsync);
        try
        {
            await Task.Run(() => _session.LoginWithCredentialsAsync(user, password, authenticator, _cts.Token));
            Log.Info($"Sesión de Steam iniciada: {user}.");
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, null);
        }
        catch (AuthenticationException ex)
        {
            SetBusy(false, null);
            ShowError(ex.Result switch
            {
                EResult.InvalidPassword => "Usuario o contraseña incorrectos.",
                EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle => "Demasiados intentos. Espera unos minutos y vuelve a probar.",
                _ => $"Steam rechazó el inicio de sesión ({ex.Result}).",
            });
        }
        catch (SteamLoginException ex)
        {
            SetBusy(false, null);
            ShowError($"Steam rechazó el inicio de sesión ({ex.Result}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Inicio de sesión en Steam: {ex.Message}");
            SetBusy(false, null);
            ShowError($"No se pudo iniciar sesión: {ex.Message}");
        }
        finally
        {
            PasswordBox.Clear();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }

    /// <summary>Steam Guard por código (app o correo): diálogo en el hilo de UI.</summary>
    private Task<string?> AskCodeAsync(string title, string message, bool previousIncorrect) =>
        Dispatcher.InvokeAsync(() =>
        {
            SetBusy(true, "Esperando el código de Steam Guard…");
            return TextPromptDialog.Ask(this, title, previousIncorrect ? $"Código incorrecto. {message}" : message);
        }).Task;

    /// <summary>Steam Guard por la app móvil: se avisa y se espera a que el usuario apruebe.</summary>
    private Task<bool> DeviceConfirmationAsync()
    {
        Dispatcher.Invoke(() => SetBusy(true, "Abre la app Steam Mobile y aprueba el inicio de sesión de «LightLauncher». Esperando…"));
        return Task.FromResult(true);
    }

    private void SetBusy(bool busy, string? status)
    {
        UserBox.IsEnabled = PasswordBox.IsEnabled = LoginButton.IsEnabled = !busy;
        StatusPanel.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = status ?? string.Empty;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
