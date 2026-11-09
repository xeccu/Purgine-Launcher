using System.Text;
using LightLauncher.Infrastructure;

namespace LightLauncher.Core;

/// <summary>
/// Anti-popups: durante un rato después de que LightLauncher arranque Steam en segundo plano, oculta la
/// ventana principal de Steam si aparece (como si se cerrara a la bandeja) y devuelve el foco a la ventana
/// que el usuario tenía delante.
/// <para>Solo actúa sobre la ventana principal, titulada exactamente «Steam». Nunca toca diálogos
/// (inicio de sesión, Steam Guard, confirmaciones de instalar/desinstalar), que necesitan al usuario.</para>
/// </summary>
public static class SteamWindowGuard
{
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static DateTime _until;
    private static IntPtr _userForeground;

    /// <summary>Vigila durante <paramref name="duration"/> (se amplía si ya estaba vigilando).</summary>
    public static void Watch(TimeSpan duration)
    {
        lock (Gate)
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (!IsSteamWindow(fg, out _)) _userForeground = fg;
            var until = DateTime.UtcNow + duration;
            if (until > _until) _until = until;
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(400));
        }
    }

    private static void Tick()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow > _until)
            {
                _timer?.Dispose();
                _timer = null;
                return;
            }
        }

        try
        {
            var stoleFocus = false;
            var fg = NativeMethods.GetForegroundWindow();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || !IsSteamWindow(hwnd, out var title)) return true;
                if (title == "Steam") // solo la ventana principal
                {
                    if (hwnd == fg) stoleFocus = true;
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                    Log.Info("Ventana principal de Steam ocultada (arranque en segundo plano).");
                }
                return true;
            }, IntPtr.Zero);

            if (stoleFocus && _userForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(_userForeground);
        }
        catch (Exception ex)
        {
            Log.Warn($"Guardia de ventanas de Steam: {ex.Message}");
        }
    }

    private static bool IsSteamWindow(IntPtr hwnd, out string title)
    {
        title = string.Empty;
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var path = ProcessUtils.TryGetProcessPath((int)pid);
        var name = path is null ? null : Path.GetFileNameWithoutExtension(path);
        if (name is null || !(name.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)))
            return false;

        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        title = sb.ToString();
        return true;
    }
}
