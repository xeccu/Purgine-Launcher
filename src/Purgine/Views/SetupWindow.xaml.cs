using System.Windows;
using Purgine.Infrastructure;
using Purgine.Updates;

namespace Purgine.Views;

/// <summary>
/// Instalador, actualizador y desinstalador de Purgine (el mismo .exe en otro modo). Instalar pide confirmación;
/// actualizar (lanzado por el auto-actualizador con <c>--update</c>) es desatendido: espera a que Purgine se cierre,
/// reemplaza el ejecutable y lo vuelve a abrir.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupRequest _request;
    private Func<Task>? _retry;
    private bool _removeProgramFolderOnClose;

    public SetupWindow(SetupRequest request)
    {
        _request = request;
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        VersionText.Text = $"Versión {AppVersion.Display} · {AppInstaller.Publisher}";
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            if (_removeProgramFolderOnClose) AppInstaller.ScheduleProgramFolderRemoval();
        };
    }

    private async Task StartAsync()
    {
        switch (_request.Mode)
        {
            case SetupMode.Install:
                Title = "Instalar Purgine";
                InstallPathText.Text = AppInstaller.InstallDir;
                if (File.Exists(AppInstaller.InstalledExe))
                {
                    ExistingText.Visibility = Visibility.Visible;
                    InstallButton.Content = "Actualizar";
                    DesktopShortcutBox.IsChecked = File.Exists(AppInstaller.DesktopShortcut);
                }
                Show(InstallPanel);
                break;
            case SetupMode.Update:
                Title = "Actualizar Purgine";
                await RunUpdateAsync();
                break;
            case SetupMode.Uninstall:
                Title = "Desinstalar Purgine";
                Show(UninstallPanel);
                break;
        }
    }

    // ===================== Instalar =====================

    private async void OnInstall(object sender, RoutedEventArgs e) => await RunInstallAsync();

    private async Task RunInstallAsync()
    {
        _retry = RunInstallAsync;
        var target = AppInstaller.InstalledExe;
        if (!EnsureNotRunning(target)) return;
        Progress("Instalando Purgine…");
        try
        {
            await AppInstaller.InstallAsync(target, DesktopShortcutBox.IsChecked == true, register: true,
                new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
            Log.Info($"Instalación completada en {target}.");
            if (LaunchAfterBox.IsChecked == true)
            {
                AppInstaller.Launch(target);
                Close();
                return;
            }
            Result("Purgine está instalado. Lo encontrarás en el menú Inicio" +
                   (DesktopShortcutBox.IsChecked == true ? " y en el Escritorio." : "."), canRetry: false);
        }
        catch (Exception ex)
        {
            Log.Error("Fallo al instalar", ex);
            Result($"No se pudo instalar: {ex.Message}", canRetry: true, showReleases: true);
        }
    }

    /// <summary>Instalación sin ventana (<c>--install --silent</c>), para despliegues por script.</summary>
    public static async Task<int> RunSilentInstallAsync()
    {
        try
        {
            var target = AppInstaller.InstalledExe;
            foreach (var p in AppInstaller.RunningInstances(target)) { using (p) await AppInstaller.WaitForProcessAsync(p.Id, TimeSpan.FromSeconds(60), killIfStuck: false); }
            await AppInstaller.InstallAsync(target, createDesktopShortcut: true, register: true, new Progress<string>(), CancellationToken.None);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en la instalación silenciosa", ex);
            return 1;
        }
    }

    // ===================== Actualizar (desatendido) =====================

    private async Task RunUpdateAsync()
    {
        _retry = RunUpdateAsync;
        var target = _request.Target is { Length: > 0 } t ? t : AppInstaller.InstalledExe;
        try
        {
            if (_request.WaitPid is int pid)
            {
                Progress("Esperando a que Purgine se cierre…");
                await AppInstaller.WaitForProcessAsync(pid, TimeSpan.FromSeconds(60), killIfStuck: true);
            }
            foreach (var p in AppInstaller.RunningInstances(target))
            {
                using (p)
                {
                    Progress("Esperando a que Purgine se cierre…");
                    await AppInstaller.WaitForProcessAsync(p.Id, TimeSpan.FromSeconds(30), killIfStuck: false);
                }
            }

            Progress($"Actualizando Purgine a la versión {AppVersion.Display}…");
            var register = AppInstaller.IsRegisteredExe(target) ||
                           (!AppInstaller.IsRegistered && string.Equals(Path.GetFullPath(target), Path.Combine(AppInstaller.DefaultInstallDir, "Purgine.exe"), StringComparison.OrdinalIgnoreCase));
            await AppInstaller.InstallAsync(target, createDesktopShortcut: false, register,
                new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
            Log.Info($"Actualización a {AppVersion.Display} completada en {target}.");
            StatusText.Text = "Listo. Abriendo Purgine…";
            AppInstaller.Launch(target);
            await Task.Delay(600);
            Close();
        }
        catch (TimeoutException)
        {
            Result("Purgine sigue abierto. Ciérralo (icono de la bandeja → Salir) y pulsa Reintentar.", canRetry: true);
        }
        catch (Exception ex)
        {
            Log.Error("Fallo al actualizar", ex);
            Result($"No se pudo actualizar: {ex.Message}\n\nTu versión anterior sigue intacta. Puedes reintentarlo o descargar el instalador desde GitHub.",
                canRetry: true, showReleases: true);
        }
    }

    // ===================== Desinstalar =====================

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        _retry = () => { OnUninstall(sender, e); return Task.CompletedTask; };
        if (!EnsureNotRunning(AppInstaller.InstalledExe)) return;
        Progress("Desinstalando Purgine…");
        AppInstaller.Uninstall(DeleteDataBox.IsChecked == true);
        _removeProgramFolderOnClose = true;
        Log.Info($"Purgine desinstalado (datos borrados: {DeleteDataBox.IsChecked == true}).");
        Result(DeleteDataBox.IsChecked == true
            ? "Purgine se ha desinstalado y tus datos se han borrado."
            : "Purgine se ha desinstalado. Tus datos siguen en %LocalAppData%\\Purgine por si vuelves a instalarlo.", canRetry: false);
    }

    // ===================== Común =====================

    private bool EnsureNotRunning(string exe)
    {
        var running = AppInstaller.RunningInstances(exe);
        foreach (var p in running) p.Dispose();
        if (running.Count == 0) return true;
        Result("Purgine está abierto. Ciérralo desde el icono de la bandeja (clic derecho → Salir) y pulsa Reintentar.", canRetry: true);
        return false;
    }

    private void Show(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { InstallPanel, UninstallPanel, ProgressPanel, ResultPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Progress(string text)
    {
        StatusText.Text = text;
        Show(ProgressPanel);
    }

    private void Result(string text, bool canRetry, bool showReleases = false)
    {
        ResultText.Text = text;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        ReleasesButton.Visibility = showReleases ? Visibility.Visible : Visibility.Collapsed;
        Show(ResultPanel);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_retry is not null) await _retry();
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e) => ShellOpen.Url(UpdateService.ReleasesPage);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
