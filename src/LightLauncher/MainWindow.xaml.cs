using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using LightLauncher.Core;
using LightLauncher.Infrastructure;
using LightLauncher.ViewModels;

namespace LightLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += async (_, _) => await _vm.RefreshAsync();
        _vm.GameLaunched += (_, _) => WindowState = WindowState.Minimized;

        // Minimizado = el usuario está jugando o no nos mira: devolvemos memoria al sistema.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                ProcessUtils.TrimCurrentProcess();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsSessionActive)
        {
            var answer = MessageBox.Show(
                "Hay una sesión de juego en curso.\n\nSi cierras LightLauncher ahora, el tiempo se registrará solo hasta este momento y Steam no se cerrará automáticamente.\n\n¿Cerrar de todos modos?",
                "LightLauncher", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }

    /// <summary>Barra de título oscura nativa (Windows 10 20H1+ / 11). Ignorado en versiones anteriores.</summary>
    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn($"Barra de título oscura no disponible: {ex.Message}");
        }
    }
}
