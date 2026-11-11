using System.Text;
using Purgine.Infrastructure;

namespace Purgine.Core;

/// <summary>
/// Monitor ligero de los diálogos de Steam previos al juego (contrato de licencia / EULA, acuerdos, términos,
/// opciones de inicio DirectX / Vulkan / modo seguro, instalación de requisitos, conflicto de Steam Cloud…).
/// <para><b>Nunca oculta nada</b> ni cambia estilos de ventana: Steam arranca con sus propios argumentos
/// (<c>-silent</c>, a la bandeja) y sus ventanas se dejan tal cual. El monitor solo hace dos cosas con un diálogo
/// que espera al usuario: si Steam lo tiene sin mostrar, <c>ShowWindow(SW_SHOW)</c>; y lo trae al frente con el
/// foco (<c>SetForegroundWindow</c>) para que el texto y los botones Aceptar / Cancelar respondan a los clics sin
/// abrir el cliente completo. Si Windows no permite cambiar el foco, hace parpadear su botón en la barra de tareas.</para>
/// </summary>
public static class SteamWindowGuard
{
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static DateTime _until;
    private static readonly HashSet<IntPtr> Focused = [];
    private static long _lastDialogTicks;

    /// <summary>Palabras de los títulos (o clases) de los diálogos previos al juego (ES / EN).</summary>
    private static readonly string[] DialogKeywords =
    [
        "eula", "licen", "agreement", "acuerdo", "contrato", "terms", "términos", "condiciones", "conditions",
        "options", "opciones", "launch", "iniciar", "inicio", "play ", "jugar", "directx", "vulkan", "opengl", "dx11", "dx12",
        "safe mode", "modo seguro", "install", "instal", "redistributable", "redistribuible", "prerequisite", "requisito",
        "preparing", "preparando", "cloud", "nube", "conflict", "conflicto", "error", "warning", "aviso", "advertencia",
    ];

    /// <summary>Ventanas de Steam que no son diálogos para el usuario (no se tocan nunca).</summary>
    private static readonly string[] IgnoredTitles =
    [
        "friends list", "lista de amigos", "chat", "notificaciones", "notifications", "big picture",
        "overlay", "default ime", "msctfime ui",
    ];

    /// <summary>¿Hay ahora un diálogo de Steam esperando al usuario? (visto hace menos de 4 s)</summary>
    public static bool IsUserDialogOpen =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDialogTicks) < TimeSpan.FromSeconds(4).Ticks;

    /// <summary>Vigila los diálogos previos al juego durante <paramref name="duration"/> (se amplía si ya vigilaba).</summary>
    public static void WatchLaunch(TimeSpan duration)
    {
        lock (Gate)
        {
            var until = DateTime.UtcNow + duration;
            if (until > _until) _until = until;
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>El juego ya arrancó: se deja de vigilar.</summary>
    public static void StopSurfacing()
    {
        lock (Gate)
        {
            _until = DateTime.MinValue;
            Focused.Clear();
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
                Focused.Clear();
                return;
            }
        }

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!IsSteamWindow(hwnd, out var title) || title.Length == 0) return true;
                var visible = NativeMethods.IsWindowVisible(hwnd);
                if (!IsUserDialog(hwnd, title, visible)) return true;

                Interlocked.Exchange(ref _lastDialogTicks, DateTime.UtcNow.Ticks);
                bool first;
                lock (Gate) first = Focused.Add(hwnd);
                if (!first && visible) return true; // ya se le dio el foco una vez: el usuario manda

                if (!visible) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                if (!NativeMethods.SetForegroundWindow(hwnd)) Flash(hwnd);
                Log.Info($"Diálogo de Steam al frente para el usuario: «{title}».");
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"Monitor de diálogos de Steam: {ex.Message}");
        }
    }

    /// <summary>
    /// ¿Es un diálogo que espera al usuario? Visible: cualquier ventana de Steam con título que no sea la ventana
    /// principal ni de las que se ignoran. Sin mostrar: solo si su título o clase es de diálogo previo al juego
    /// (así nunca se destapan ventanas internas de Steam).
    /// </summary>
    private static bool IsUserDialog(IntPtr hwnd, string title, bool visible)
    {
        var lower = title.ToLowerInvariant();
        if (IgnoredTitles.Any(lower.Contains)) return false;
        if (IsMainWindow(hwnd, title)) return false;
        var cls = ClassName(hwnd).ToLowerInvariant();
        if (cls is "ime" or "msctfime ui" || cls.StartsWith(".net-broadcasteventwindow", StringComparison.Ordinal)) return false;

        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var isToolWindow = (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & NativeMethods.WS_EX_APPWINDOW) == 0;
        var keyword = DialogKeywords.Any(lower.Contains) || DialogKeywords.Any(cls.Contains);
        if (isToolWindow && !keyword) return false; // notificaciones y auxiliares

        if (NativeMethods.GetWindowRect(hwnd, out var r) && !NativeMethods.IsIconic(hwnd))
        {
            var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
            if (w < 200 || h < 100) return false;
        }
        return visible || keyword;
    }

    /// <summary>La ventana principal del cliente (título «Steam» y tamaño completo, o minimizada sin dueña): no se toca.</summary>
    private static bool IsMainWindow(IntPtr hwnd, string title)
    {
        if (!title.Equals("Steam", StringComparison.Ordinal)) return false;
        if (NativeMethods.IsIconic(hwnd)) return NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == IntPtr.Zero;
        return NativeMethods.GetWindowRect(hwnd, out var r) && r.Right - r.Left >= 760 && r.Bottom - r.Top >= 480;
    }

    /// <summary>Parpadeo del botón en la barra de tareas (si Windows no deja cambiar el foco desde otro programa).</summary>
    private static void Flash(IntPtr hwnd)
    {
        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        NativeMethods.FlashWindowEx(ref info);
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
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
