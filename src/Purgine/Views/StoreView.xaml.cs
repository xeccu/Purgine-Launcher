using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.ViewModels;

namespace Purgine.Views;

/// <summary>
/// Pestaña Tienda: la tienda oficial de Steam en un WebView2 (motor de Microsoft Edge).
/// <para>
/// Consumo: el navegador vive en procesos aparte (<c>msedgewebview2.exe</c>). Se crea solo al abrir la pestaña;
/// al salir de ella se suspende (sin CPU, memoria marcada como prescindible) y, si no se vuelve en
/// <see cref="ReleaseAfter"/>, se cierra del todo y sus procesos terminan. También se cierra al ocultar la
/// ventana en la bandeja. Al volver se reabre en la última página.
/// </para>
/// Los enlaces <c>steam://</c> (Instalar, Jugar…) no despiertan el cliente de Steam: los resuelve Purgine.
/// </summary>
public partial class StoreView : UserControl
{
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromSeconds(60);
    private const string RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private readonly DispatcherTimer _releaseTimer;
    private StoreViewModel? _vm;
    private WebView2? _web;
    private int _generation;
    private string? _resumeUrl;

    public StoreView()
    {
        InitializeComponent();
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReleaseAfter };
        _releaseTimer.Tick += (_, _) =>
        {
            _releaseTimer.Stop();
            if (_vm?.IsActive != true) ReleaseBrowser($"{ReleaseAfter.TotalSeconds:0} s fuera de la pestaña");
        };
        DataContextChanged += OnDataContextChanged;
    }

    // =====================================================================================
    // Enlace con el ViewModel
    // =====================================================================================

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.NavigationRequested -= OnNavigationRequested;
            _vm.ReleaseRequested -= OnReleaseRequested;
        }
        _vm = e.NewValue as StoreViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.ReleaseRequested += OnReleaseRequested;
        if (_vm.IsActive) _ = ActivateAsync();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoreViewModel.IsCovered) && _web is not null && _vm is not null)
        {
            _web.Visibility = _vm.IsCovered ? Visibility.Hidden : Visibility.Visible;
            return;
        }
        if (e.PropertyName != nameof(StoreViewModel.IsActive) || _vm is null) return;
        if (_vm.IsActive) _ = ActivateAsync();
        else Deactivate();
    }

    private void OnNavigationRequested(string url)
    {
        if (_web?.CoreWebView2 is not { } core) return; // se recoge con TakePendingUrl al crear el navegador
        _vm?.TakePendingUrl();
        core.Navigate(url);
    }

    private void OnReleaseRequested() => ReleaseBrowser("ventana oculta o cierre");

    // =====================================================================================
    // Ciclo de vida del navegador
    // =====================================================================================

    private async Task ActivateAsync()
    {
        _releaseTimer.Stop();
        if (_web?.CoreWebView2 is { } core)
        {
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                if (core.IsSuspended) core.Resume(); // al hacerse visible también se reanuda solo
                if (_vm?.TakePendingUrl() is { } pending) core.Navigate(pending);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tienda: no se pudo reanudar el navegador: {ex.Message}");
            }
            return;
        }
        await EnsureBrowserAsync();
    }

    /// <summary>Fuera de la pestaña: suspender ya y programar el cierre completo.</summary>
    private void Deactivate()
    {
        if (_web is null) return;
        _releaseTimer.Stop();
        _releaseTimer.Start();

        // Tras el pase de layout el control ya está oculto (TrySuspendAsync exige que no sea visible).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            if (_vm?.IsActive == true || _web?.CoreWebView2 is not { } core) return;
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                if (!IsVisible && !core.IsSuspended)
                {
                    var suspended = await core.TrySuspendAsync();
                    Log.Info($"Tienda: navegador {(suspended ? "suspendido" : "en segundo plano (no se pudo suspender)")}.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Tienda: no se pudo suspender el navegador: {ex.Message}");
            }
        }));
    }

    private async Task EnsureBrowserAsync()
    {
        if (_web is not null || _vm is null) return;
        var generation = ++_generation;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StartingPanel.Visibility = Visibility.Visible;

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString(); // lanza si falta el runtime
            var options = new CoreWebView2EnvironmentOptions { Language = "es-ES" };
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir, options);
            if (generation != _generation) return;

            var web = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0E, 0x10, 0x14),
                Visibility = _vm.IsCovered ? Visibility.Hidden : Visibility.Visible,
            };
            _web = web;
            Host.Children.Add(web);
            await web.EnsureCoreWebView2Async(environment);
            if (generation != _generation) return; // se liberó mientras arrancaba

            var core = web.CoreWebView2;
            var settings = core.Settings;
            settings.IsStatusBarEnabled = false;
            settings.IsPasswordAutosaveEnabled = false; // la contraseña de Steam no se guarda en ningún sitio
            settings.IsGeneralAutofillEnabled = false;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
#endif
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.SourceChanged += (_, _) => UpdateNavigationState();
            core.HistoryChanged += (_, _) => UpdateNavigationState();
            core.NewWindowRequested += OnNewWindowRequested;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.ProcessFailed += OnProcessFailed;

            core.Navigate(_vm.TakePendingUrl() ?? _resumeUrl ?? StoreViewModel.HomeUrl);
            _resumeUrl = null;
            StartingPanel.Visibility = Visibility.Collapsed;
            Log.Info($"Tienda: navegador WebView2 {environment.BrowserVersionString} iniciado.");

            if (_vm.IsActive != true) Deactivate();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            DisposeBrowser();
            ShowError("Falta Microsoft Edge WebView2",
                "La tienda usa el motor de Microsoft Edge (WebView2), que no está instalado en este equipo. " +
                "Viene con Windows 11; en Windows 10 se instala gratis desde Microsoft.", offerRuntime: true);
        }
        catch (Exception ex) when (generation == _generation)
        {
            Log.Error("Tienda: no se pudo iniciar WebView2", ex);
            DisposeBrowser();
            ShowError("No se pudo abrir la tienda", ex.Message, offerRuntime: false);
        }
        catch (Exception)
        {
            // El navegador se liberó mientras arrancaba: nada que mostrar.
        }
    }

    /// <summary>Cierra el navegador (sus procesos terminan) recordando la página para reabrirla.</summary>
    private void ReleaseBrowser(string reason)
    {
        _releaseTimer.Stop();
        if (_web is null) return;
        try { _resumeUrl = _web.CoreWebView2?.Source; } catch (Exception) { /* ya no responde */ }
        DisposeBrowser();
        StartingPanel.Visibility = Visibility.Visible;
        Log.Info($"Tienda: navegador cerrado ({reason}).");
        ProcessUtils.TrimCurrentProcess();
    }

    private void DisposeBrowser()
    {
        _generation++;
        var web = _web;
        _web = null;
        LoadingBar.Visibility = Visibility.Collapsed;
        BackButton.IsEnabled = ForwardButton.IsEnabled = false;
        if (web is null) return;
        Host.Children.Remove(web);
        try { web.Dispose(); }
        catch (Exception ex) { Log.Warn($"Tienda: error al cerrar el navegador: {ex.Message}"); }
    }

    private void ShowError(string title, string detail, bool offerRuntime)
    {
        StartingPanel.Visibility = Visibility.Collapsed;
        ErrorTitle.Text = title;
        ErrorDetail.Text = detail;
        RuntimeButton.Visibility = offerRuntime ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    // =====================================================================================
    // Eventos del navegador
    // =====================================================================================

    private static bool IsSteamLink(string? uri) => uri?.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) == true;

    private void HandleSteamLink(string uri)
    {
        var core = _web?.CoreWebView2;
        var redirect = _vm?.HandleSteamLink(uri, core?.DocumentTitle);
        if (redirect is not null) core?.Navigate(redirect);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsSteamLink(e.Uri))
        {
            e.Cancel = true;
            HandleSteamLink(e.Uri);
            return;
        }
        LoadingBar.Visibility = Visibility.Visible;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        UpdateNavigationState();
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            Log.Warn($"Tienda: la página no cargó ({e.WebErrorStatus}).");
    }

    /// <summary>Ventanas emergentes (target=_blank): se abren en la misma pestaña.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsSteamLink(e.Uri)) HandleSteamLink(e.Uri);
        else _web?.CoreWebView2?.Navigate(e.Uri);
    }

    /// <summary>Protocolos externos: steam:// lo resuelve Purgine; el resto no se abre desde la tienda.</summary>
    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        if (IsSteamLink(e.Uri)) HandleSteamLink(e.Uri);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warn($"Tienda: fallo del proceso del navegador ({e.ProcessFailedKind}).");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            _web?.CoreWebView2?.Reload();
            return;
        }
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            ReleaseBrowser("el proceso del navegador terminó");
            if (_vm?.IsActive == true) _ = EnsureBrowserAsync();
        }
    }

    private void UpdateNavigationState()
    {
        if (_web?.CoreWebView2 is not { } core) return;
        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
        AddressText.Text = core.Source;
    }

    // =====================================================================================
    // Barra de navegación
    // =====================================================================================

    private void OnBack(object sender, RoutedEventArgs e) { if (_web?.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); }

    private void OnForward(object sender, RoutedEventArgs e) { if (_web?.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); }

    private void OnReload(object sender, RoutedEventArgs e) => _web?.CoreWebView2?.Reload();

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2 is { } core) core.Navigate(StoreViewModel.HomeUrl);
        else _vm?.Navigate(StoreViewModel.HomeUrl);
    }

    private void OnOpenExternal(object sender, RoutedEventArgs e)
    {
        string url;
        try { url = _web?.CoreWebView2?.Source ?? StoreViewModel.HomeUrl; }
        catch (Exception) { url = StoreViewModel.HomeUrl; }
        OpenInBrowser(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : StoreViewModel.HomeUrl);
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = EnsureBrowserAsync();

    private void OnGetRuntime(object sender, RoutedEventArgs e) => OpenInBrowser(RuntimeUrl);

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"No se pudo abrir {url}: {ex.Message}"); }
    }
}
