using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Purgine.EpicNet;
using Purgine.Infrastructure;

namespace Purgine.Views;

/// <summary>
/// Inicio de sesión en Epic Games con su página oficial dentro de un WebView2. El usuario escribe sus datos en
/// la web de Epic; al terminar, Epic redirige a una página que muestra un código de autorización de un solo uso,
/// que se canjea por tokens. Purgine nunca ve la contraseña. El navegador se cierra al acabar.
/// </summary>
public partial class EpicLoginDialog : Window
{
    private readonly EpicAccountService _epic;
    private WebView2? _web;
    private bool _done;

    private EpicLoginDialog(EpicAccountService epic)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        _epic = epic;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => { _web?.Dispose(); _web = null; };
    }

    /// <summary>True si la cuenta quedó vinculada.</summary>
    public static bool Show(Window? owner, EpicAccountService epic) =>
        new EpicLoginDialog(epic) { Owner = owner }.ShowDialog() == true;

    private async Task StartAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, System.IO.Path.Combine(AppPaths.DataDir, "webview-epic"),
                new CoreWebView2EnvironmentOptions { Language = "es-ES" });
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0E, 0x10, 0x14) };
            Host.Children.Add(_web);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            _web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            _web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; _web.CoreWebView2.Navigate(e.Uri); };
            _web.CoreWebView2.Navigate(EpicAccountService.LoginUrl);
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusText.Text = "Falta Microsoft Edge WebView2, necesario para iniciar sesión en Epic.";
        }
        catch (Exception ex)
        {
            Log.Error("Epic: no se pudo abrir la página de inicio de sesión", ex);
            StatusText.Text = $"No se pudo abrir la página de Epic: {ex.Message}";
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_done || _web?.CoreWebView2 is not { } core) return;
        if (!core.Source.StartsWith(EpicAccountService.RedirectPrefix, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            // La página de redirección es un JSON: {"redirectUrl": …, "authorizationCode": "…", "sid": …}
            var raw = await core.ExecuteScriptAsync("document.body.innerText");
            var text = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("authorizationCode", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(code))
            {
                core.Navigate(EpicAccountService.LoginUrl); // aún sin sesión: volver al formulario
                return;
            }

            _done = true;
            InfoText.Text = "Vinculando tu cuenta de Epic Games…";
            await _epic.LoginWithAuthorizationCodeAsync(code, CancellationToken.None);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is JsonException or EpicApiException)
        {
            _done = false;
            Log.Warn($"Epic: no se pudo completar el inicio de sesión: {ex.Message}");
            InfoText.Text = $"No se pudo completar el inicio de sesión: {ex.Message}. Vuelve a intentarlo.";
            core.Navigate(EpicAccountService.LoginUrl);
        }
    }
}
