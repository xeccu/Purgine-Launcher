using System.Windows;
using System.Windows.Interop;

namespace LightLauncher.Infrastructure;

/// <summary>Ajustes visuales comunes a todas las ventanas.</summary>
public static class WindowTheming
{
    /// <summary>Barra de título oscura nativa (Windows 10 20H1+ / 11). Se ignora en versiones anteriores.</summary>
    public static void UseDarkTitleBar(Window window)
    {
        void Apply()
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int enabled = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Warn($"Barra de título oscura no disponible: {ex.Message}");
            }
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }
}
