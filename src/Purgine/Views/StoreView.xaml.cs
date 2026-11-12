using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.ItchNet;
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
            _vm.RedeemRequested -= OnRedeemRequested;
        }
        _vm = e.NewValue as StoreViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.ReleaseRequested += OnReleaseRequested;
        _vm.RedeemRequested += OnRedeemRequested;
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
            core.DownloadStarting += OnDownloadStarting;
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ItchSameTabScript);

            core.Navigate(_vm.TakePendingUrl() ?? _resumeUrl ?? _vm.CurrentHomeUrl);
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

    // =====================================================================================
    // itch.io: las descargas de la web pasan a la cola de Purgine (no al gestor de descargas del navegador)
    // =====================================================================================

    private static bool IsItchHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith("itch.io", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith("itch.zone", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith("hwcdn.net", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Contains("itchio-mirror", StringComparison.OrdinalIgnoreCase)); // almacenamiento de itch en Cloudflare R2

    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var page = _web?.CoreWebView2?.Source;
        var uri = e.DownloadOperation.Uri;
        if (!IsItchHost(uri) && !IsItchHost(page)) return; // el resto, como siempre
        e.Cancel = true;   // nada de gestor de descargas del navegador: lo descarga Purgine
        e.Handled = true;
        var size = (long)(e.DownloadOperation.TotalBytesToReceive ?? 0);
        var fileName = Path.GetFileName(e.ResultFilePath);
        string? title = null, image = null;
        // Datos de la ficha del juego visitada (la página de descargas suele llamarse solo «itch.io»).
        var game = ItchWebScraper.GameUrlOf(page) ?? _lastItchGame?.GameUrl;
        if (_lastItchGame is { } last && last.GameUrl == game)
        {
            title = last.Title;
            image = last.Cover;
        }
        try
        {
            var raw = await _web!.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                  const meta = p => document.querySelector(`meta[property="${p}"]`)?.content || '';
                  const h = document.querySelector('.game_title, h1')?.innerText || '';
                  return JSON.stringify({ title: meta('og:title') || h || document.title, image: meta('og:image') });
                })()
                """);
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            if (json is not null && title is null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var pageTitle = doc.RootElement.GetProperty("title").GetString();
                if (IsUsefulTitle(pageTitle)) title = pageTitle;
                image ??= doc.RootElement.GetProperty("image").GetString() is { Length: > 0 } img ? img : null;
            }
        }
        catch (Exception ex) { Log.Warn($"itch.io: datos de la página no disponibles ({ex.Message})."); }
        // Sin título útil: el slug de la dirección, legible («mi-juego-genial» → «Mi Juego Genial»).
        title ??= game is null ? null : ItchWebScraper.PrettySlug(game.Split('/').Last());
        _vm?.CaptureItchDownload(new ItchCapturedDownload(uri, fileName, game ?? page ?? uri, title, image, size));
    }

    /// <summary>
    /// itch.io abre la página de descargas con <c>window.open()</c> vacío y después le pone la dirección (o abre
    /// pestañas nuevas). En la tienda todo va en la misma pestaña: este script hace que <c>window.open</c> navegue aquí,
    /// también cuando la dirección llega después, en lugar de quedarse en <c>about:blank</c>.
    /// </summary>
    private const string ItchSameTabScript = """
        (() => {
          if (!/(^|\.)itch\.io$/i.test(location.hostname)) return;
          const go = u => { if (u && String(u) !== 'about:blank') location.href = String(u); };
          window.open = function (url) {
            if (url && String(url) !== 'about:blank') { go(url); return window; }
            const loc = { assign: go, replace: go, set href(v) { go(v); }, get href() { return 'about:blank'; }, toString() { return 'about:blank'; } };
            const fake = { closed: false, opener: window, close() {}, focus() {}, blur() {}, postMessage() {},
                           document: { write() {}, writeln() {}, open() {}, close() {}, body: null } };
            Object.defineProperty(fake, 'location', { get() { return loc; }, set(v) { go(v); } });
            return fake;
          };
        })();
        """;

    /// <summary>Última ficha de juego de itch.io visitada (para nombrar bien lo que se descargue después).</summary>
    private (string GameUrl, string? Title, string? Cover)? _lastItchGame;

    /// <summary>Al cargar una página de itch.io de un juego, se recuerdan su título real y su portada.</summary>
    private async Task RememberItchGameAsync()
    {
        if (_web?.CoreWebView2 is not { } core || ItchWebScraper.GameUrlOf(core.Source) is not { } game) return;
        // Solo la ficha del juego (autor.itch.io/juego): las subpáginas (/download/…, /purchase) no tienen su
        // título limpio ni su portada y no deben pisar lo que ya se sabe.
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var src) && src.AbsolutePath.Trim('/').Contains('/')) return;
        try
        {
            var raw = await core.ExecuteScriptAsync("""
                (() => {
                  const meta = p => document.querySelector(`meta[property="${p}"]`)?.content || '';
                  const h = document.querySelector('h1.game_title')?.innerText || '';
                  return JSON.stringify({ title: h || meta('og:title'), image: meta('og:image') });
                })()
                """);
            if (System.Text.Json.JsonSerializer.Deserialize<string>(raw) is not { } json) return;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var title = doc.RootElement.GetProperty("title").GetString();
            var image = doc.RootElement.GetProperty("image").GetString();
            if (IsUsefulTitle(title))
                _lastItchGame = (game, title!.Trim(), string.IsNullOrWhiteSpace(image) ? null : image);
            else if (_lastItchGame?.GameUrl != game)
                _lastItchGame = (game, null, string.IsNullOrWhiteSpace(image) ? null : image);
        }
        catch (Exception ex) { Log.Warn($"itch.io: no se pudo leer la ficha del juego ({ex.Message})."); }
    }

    private static bool IsUsefulTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && !title.Trim().Equals("itch.io", StringComparison.OrdinalIgnoreCase) &&
        !title.Contains("Download ", StringComparison.Ordinal) || title?.Contains(" by ", StringComparison.Ordinal) == true;

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
        _navigationDone?.TrySetResult();
        if (e.IsSuccess) _ = RememberItchGameAsync();
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
        // Ventana vacía (window.open() que recibe la dirección después): no se navega a about:blank; el script de la
        // página lleva esa dirección a esta misma pestaña.
        else if (!string.IsNullOrEmpty(e.Uri) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            _web?.CoreWebView2?.Navigate(e.Uri);
    }

    /// <summary>Protocolos externos: steam:// lo resuelve Purgine; el resto no se abre desde la tienda.</summary>
    /// <summary>
    /// Protocolos externos: steam:// lo resuelve Purgine; com.epicgames.launcher:// (botones «Instalar» o «Abrir»
    /// de la tienda de Epic) se deja pasar a Epic Games Launcher, que es quien instala esos juegos; el resto no se abre.
    /// </summary>
    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        if (e.Uri.StartsWith("com.epicgames.launcher:", StringComparison.OrdinalIgnoreCase))
        {
            // Purgine no abre Epic Games Launcher. Los juegos de Epic ya instalados se juegan desde la Biblioteca.
            _vm?.ReportEpicLauncherLink();
            return;
        }
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
        _vm?.SyncStoreFromUrl(core.Source);
    }

    // =====================================================================================
    // Barra de navegación
    // =====================================================================================

    // =====================================================================================
    // Canjear clave
    // =====================================================================================

    private TaskCompletionSource? _navigationDone;

    private void OnKeyBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && _vm?.RedeemCommand.CanExecute(null) == true) _vm.RedeemCommand.Execute(null);
    }

    private void OnOpenAgreement(object sender, RoutedEventArgs e) => OpenInBrowser(StoreViewModel.SteamSubscriberAgreementUrl);

    private async void OnRedeemRequested(RedeemRequest request)
    {
        try { await RedeemAsync(request); }
        catch (Exception ex)
        {
            Log.Warn($"Canjear clave: {ex.Message}");
            _vm?.ReportRedeem($"No se pudo completar: {ex.Message}");
        }
    }

    /// <summary>
    /// Rellena la página oficial de canje (y la envía si procede). La clave nunca sale del equipo salvo hacia la
    /// propia página de Steam / Epic, con la sesión de la tienda.
    /// </summary>
    private async Task RedeemAsync(RedeemRequest request)
    {
        if (_web?.CoreWebView2 is null) await EnsureBrowserAsync();
        if (_web?.CoreWebView2 is not { } core) { _vm?.ReportRedeem("El navegador no está disponible."); return; }

        var target = request.Platform == RedeemPlatform.Epic ? StoreViewModel.EpicRedeemUrl : StoreViewModel.SteamRedeemUrl;
        var host = new Uri(target).Host;
        if (!core.Source.Contains(host, StringComparison.OrdinalIgnoreCase) || !core.Source.Contains("redeem", StringComparison.OrdinalIgnoreCase) &&
            !core.Source.Contains("registerkey", StringComparison.OrdinalIgnoreCase))
        {
            _navigationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Navigate(target);
            await Task.WhenAny(_navigationDone.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        }

        var keyJson = System.Text.Json.JsonSerializer.Serialize(request.Key);
        if (request.Platform == RedeemPlatform.Steam) await RedeemSteamAsync(core, keyJson, request.Submit);
        else await RedeemEpicAsync(core, keyJson);
    }

    private static async Task<string?> RunJsonAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script);
        // ExecuteScriptAsync devuelve el valor como JSON: una cadena llega entre comillas.
        return raw is null or "null" ? null : System.Text.Json.JsonSerializer.Deserialize<string>(raw);
    }

    private async Task RedeemSteamAsync(CoreWebView2 core, string keyJson, bool submit)
    {
        string? state = null;
        for (var i = 0; i < 20; i++)
        {
            state = await RunJsonAsync(core, $$"""
                (() => {
                  if (/login/i.test(location.pathname)) return 'login';
                  const input = document.getElementById('product_key');
                  if (!input) return 'wait';
                  input.focus(); input.value = {{keyJson}};
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  if ({{(submit ? "true" : "false")}}) {
                    const ssa = document.getElementById('accept_ssa');
                    if (ssa) { ssa.checked = true; ssa.dispatchEvent(new Event('change', { bubbles: true })); }
                    if (typeof RegisterProductKey === 'function') RegisterProductKey();
                    else document.getElementById('register_btn')?.click();
                    return 'submitted';
                  }
                  return 'filled';
                })()
                """);
            if (state != "wait") break;
            await Task.Delay(500);
        }
        switch (state)
        {
            case "login":
                _vm?.ReportRedeem("Inicia sesión en Steam en la página de abajo y vuelve a pulsar «Canjear clave».");
                return;
            case "filled":
                _vm?.ReportRedeem("Clave rellenada en la página. Marca el Acuerdo de Suscriptor (aquí o abajo) para canjearla.");
                return;
            case not "submitted":
                _vm?.ReportRedeem("La página de canje de Steam no respondió. Puedes terminar el canje abajo.");
                return;
        }

        // Resultado: error de Steam (clave no válida, ya canjeada…) o recibo con los productos.
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(700);
            var result = await RunJsonAsync(core, """
                (() => {
                  const vis = el => el && el.offsetParent !== null && el.innerText.trim().length > 0;
                  const err = document.getElementById('error_display');
                  if (vis(err)) return 'error:' + err.innerText.trim();
                  const receipt = document.getElementById('receipt_form');
                  if (vis(receipt)) {
                    const list = document.getElementById('registerkey_productlist');
                    return 'ok:' + (list ? list.innerText.trim().replace(/\s+/g, ' ') : '');
                  }
                  return '';
                })()
                """);
            if (string.IsNullOrEmpty(result)) continue;
            if (result.StartsWith("ok:", StringComparison.Ordinal))
            {
                var products = result[3..];
                _vm?.ReportRedeem(products.Length > 0 ? $"✔ Clave canjeada: {products}" : "✔ Clave canjeada en tu cuenta de Steam.", succeeded: true);
            }
            else _vm?.ReportRedeem($"Steam: {result[6..]}");
            return;
        }
        _vm?.ReportRedeem("Enviada. Mira el resultado en la página de abajo.");
    }

    private async Task RedeemEpicAsync(CoreWebView2 core, string keyJson)
    {
        string? state = null;
        for (var i = 0; i < 24; i++)
        {
            state = await RunJsonAsync(core, $$"""
                (() => {
                  if (/login|id\/login/i.test(location.href)) return 'login';
                  const inputs = [...document.querySelectorAll('input')].filter(x => x.offsetParent !== null &&
                    (x.type === 'text' || x.type === '' || /code|redeem/i.test(x.name + x.id + (x.getAttribute('aria-label') || ''))));
                  const input = inputs[0];
                  if (!input) return 'wait';
                  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                  input.focus(); setter.call(input, {{keyJson}});
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                  return 'filled';
                })()
                """);
            if (state != "wait") break;
            await Task.Delay(500);
        }
        if (state == "login") { _vm?.ReportRedeem("Inicia sesión en Epic Games en la página de abajo y vuelve a pulsar «Canjear clave»."); return; }
        if (state != "filled") { _vm?.ReportRedeem("La página de canje de Epic no respondió. Puedes terminar el canje abajo."); return; }

        await Task.Delay(600); // la página habilita el botón al validar el formato
        var clicked = await RunJsonAsync(core, """
            (() => {
              const btn = [...document.querySelectorAll('button')].find(b => !b.disabled && b.offsetParent !== null &&
                /^(canjear|redeem)$/i.test(b.innerText.trim()));
              if (!btn) return 'nobutton';
              btn.click(); return 'clicked';
            })()
            """);
        _vm?.ReportRedeem(clicked == "clicked"
            ? "Código enviado a Epic. Si la página pide confirmar el producto, termina el canje abajo."
            : "Código rellenado. Pulsa «Canjear» en la página de abajo para terminar.");
    }

    private void OnBack(object sender, RoutedEventArgs e) { if (_web?.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); }

    private void OnForward(object sender, RoutedEventArgs e) { if (_web?.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); }

    private void OnReload(object sender, RoutedEventArgs e) => _web?.CoreWebView2?.Reload();

    private void OnHome(object sender, RoutedEventArgs e)
    {
        var home = _vm?.CurrentHomeUrl ?? StoreViewModel.HomeUrl;
        if (_web?.CoreWebView2 is { } core) core.Navigate(home);
        else _vm?.Navigate(home);
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
