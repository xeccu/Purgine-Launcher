using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Purgine.Infrastructure;

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

    /// <summary>¿Admite el sistema el material Mica para ventanas (Windows 11 22H2, compilación 22621+)?</summary>
    public static bool SupportsMica => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Activa o desactiva el material Mica de Windows 11 (fondo desenfocado del escritorio) detrás de la
    /// ventana. Con Mica, el fondo de la ventana pasa a transparente y solo las zonas pintadas con pinceles
    /// translúcidos (barra lateral, cabecera) lo dejan ver. Devuelve false si el sistema no lo admite.
    /// </summary>
    public static bool SetMica(Window window, bool enabled, Brush opaqueBackground)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || HwndSource.FromHwnd(hwnd) is not { CompositionTarget: { } target }) return false;
        if (enabled && !SupportsMica) enabled = false;
        try
        {
            var margins = enabled
                ? new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : new NativeMethods.MARGINS();
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int type = enabled ? 2 : 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
            target.BackgroundColor = enabled ? Colors.Transparent : SystemColors.WindowColor;
            window.Background = enabled ? Brushes.Transparent : opaqueBackground;
            return enabled;
        }
        catch (Exception ex)
        {
            Log.Warn($"Material Mica no disponible: {ex.Message}");
            target.BackgroundColor = SystemColors.WindowColor;
            window.Background = opaqueBackground;
            return false;
        }
    }
}
