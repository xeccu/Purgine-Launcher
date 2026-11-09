using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LightLauncher.Infrastructure;

/// <summary>
/// Icono en la bandeja del sistema implementado directamente sobre <c>Shell_NotifyIcon</c>.
/// Equivale al <c>NotifyIcon</c> de WinForms pero sin cargar System.Windows.Forms en el proceso
/// (varios MB menos de RAM). Menú contextual nativo de Win32 (<c>TrackPopupMenuEx</c>): rápido y sin
/// los problemas de foco de un ContextMenu de WPF abierto desde la bandeja.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int CallbackMessage = 0x8000 + 0x42; // WM_APP + n

    private const int CmdRestore = 1;
    private const int CmdRefresh = 2;
    private const int CmdExit = 3;

    private readonly HwndSource _window;
    private readonly int _taskbarCreatedMessage;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private string _tooltip;
    private bool _added;
    private bool _disposed;

    public event EventHandler? RestoreRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;

    public TrayIcon(string tooltip)
    {
        _tooltip = Truncate(tooltip, 127);

        // Ventana oculta de nivel superior (no "message-only": esas no reciben el broadcast TaskbarCreated).
        _window = new HwndSource(new HwndSourceParameters("LightLauncherTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // sin WS_VISIBLE
        });
        _window.AddHook(WndProc);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        (_icon, _ownsIcon) = LoadAppIcon();
        Add();
    }

    /// <summary>Texto al pasar el ratón por el icono.</summary>
    public void SetTooltip(string text)
    {
        _tooltip = Truncate(text, 127);
        if (!_added) return;
        var data = CreateData(NativeMethods.NIF_TIP);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>Notificación de Windows (globo / toast) anclada al icono.</summary>
    public void ShowNotification(string title, string text)
    {
        if (!_added) return;
        var data = CreateData(NativeMethods.NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NativeMethods.NIIF_INFO | NativeMethods.NIIF_NOSOUND;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_added) Log.Warn("No se pudo añadir el icono de la bandeja.");
    }

    private NativeMethods.NOTIFYICONDATA CreateData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    RestoreRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            Add(); // Explorer se reinició: hay que volver a registrar el icono
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)CmdRestore, "Restaurar");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)CmdRefresh, "Actualizar biblioteca");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)CmdExit, "Salir");
            NativeMethods.SetMenuDefaultItem(menu, CmdRestore, 0);

            NativeMethods.GetCursorPos(out var pt);
            // Necesario para que el menú se cierre al hacer clic fuera (comportamiento documentado de Win32).
            NativeMethods.SetForegroundWindow(_window.Handle);
            var cmd = NativeMethods.TrackPopupMenuEx(menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN,
                pt.X, pt.Y, _window.Handle, IntPtr.Zero);
            NativeMethods.PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CmdRestore: RestoreRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdRefresh: RefreshRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdExit: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /// <summary>Icono pequeño del propio .exe (ApplicationIcon); si no hay, el genérico de Windows.</summary>
    private static (IntPtr icon, bool owned) LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var small = new IntPtr[1];
                if (NativeMethods.ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return (small[0], true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo extraer el icono de la app: {ex.Message}");
        }
        return (NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION), false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = CreateData(0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        }
        if (_ownsIcon) NativeMethods.DestroyIcon(_icon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
