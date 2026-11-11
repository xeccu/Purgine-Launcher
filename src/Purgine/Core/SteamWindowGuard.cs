using System.Text;
using Purgine.Infrastructure;

namespace Purgine.Core;

/// <summary>
/// Ventanas de Steam mientras Purgine lo usa en segundo plano:
/// <list type="bullet">
///   <item><b>Anti-popups</b>: oculta la ventana principal de Steam si aparece (como si se cerrara a la bandeja) y
///         devuelve el foco a la ventana que el usuario tenía delante. Solo la principal: titulada «Steam» y de
///         tamaño de ventana completa.</item>
///   <item><b>Diálogos previos al juego</b>: contrato de licencia (EULA), acuerdos, selector de opciones de inicio
///         (DirectX / Vulkan / modo seguro), instalación de requisitos, conflicto de Steam Cloud, errores… Nunca se
///         ocultan; si Steam los dejó escondidos (pertenecen a la ventana principal, que está minimizada u oculta)
///         se muestran flotando, y se traen al frente una vez para que el usuario pueda aceptar sin abrir Steam.</item>
/// </list>
/// </summary>
public static class SteamWindowGuard
{
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static DateTime _hideMainUntil;
    private static DateTime _surfaceUntil;
    private static IntPtr _userForeground;
    private static readonly HashSet<IntPtr> Surfaced = [];
    private static long _lastDialogTicks;

    /// <summary>Palabras de los títulos de los diálogos que piden algo al usuario antes de jugar (ES / EN).</summary>
    private static readonly string[] DialogKeywords =
    [
        "eula", "licen", "agreement", "acuerdo", "contrato", "terms", "términos", "condiciones", "conditions",
        "launch", "iniciar", "inicio", "opciones", "options", "play ", "jugar", "directx", "vulkan", "opengl", "dx11", "dx12",
        "safe mode", "modo seguro", "install", "instal", "redistributable", "redistribuible", "prerequisite", "requisito",
        "preparing", "preparando", "update", "actualiz", "cloud", "nube", "conflict", "conflicto", "sync",
        "error", "warning", "aviso", "advertencia", "steam guard", "confirm",
    ];

    /// <summary>Ventanas de Steam que no son diálogos para el usuario (no se tocan nunca).</summary>
    private static readonly string[] IgnoredTitles =
    [
        "friends list", "lista de amigos", "amigos", "chat", "notificaciones", "notifications", "steam big picture",
        "sp overlay", "steam overlay", "default ime", "msctfime ui",
    ];

    /// <summary>Clases de ventana de primer nivel de la interfaz de Steam (nueva, VGUI clásica y diálogos estándar).</summary>
    private static readonly string[] DialogClasses = ["SDL_app", "vguiPopupWindow", "#32770"];

    /// <summary>¿Hay ahora un diálogo de Steam esperando al usuario? (visto hace menos de 4 s)</summary>
    public static bool IsUserDialogOpen =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDialogTicks) < TimeSpan.FromSeconds(4).Ticks;

    /// <summary>Oculta la ventana principal de Steam durante <paramref name="duration"/> (Steam recién arrancado).</summary>
    public static void Watch(TimeSpan duration) => Start(duration, TimeSpan.Zero);

    /// <summary>
    /// Mientras se lanza un juego: mantiene oculta la ventana principal y muestra/trae al frente los diálogos
    /// previos al juego durante <paramref name="duration"/>.
    /// </summary>
    public static void WatchLaunch(TimeSpan duration) => Start(duration, duration);

    /// <summary>El juego ya arrancó: dejar de buscar diálogos (la ventana principal se sigue ocultando lo previsto).</summary>
    public static void StopSurfacing()
    {
        lock (Gate) _surfaceUntil = DateTime.MinValue;
    }

    private static void Start(TimeSpan hideMain, TimeSpan surface)
    {
        lock (Gate)
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (!IsSteamWindow(fg, out _)) _userForeground = fg;
            var now = DateTime.UtcNow;
            if (now + hideMain > _hideMainUntil) _hideMainUntil = now + hideMain;
            if (surface > TimeSpan.Zero)
            {
                if (now + surface > _surfaceUntil) _surfaceUntil = now + surface;
                Surfaced.Clear();
            }
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(400));
        }
    }

    private static void Tick()
    {
        bool hideMain, surface;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            hideMain = now <= _hideMainUntil;
            surface = now <= _surfaceUntil;
            if (!hideMain && !surface)
            {
                _timer?.Dispose();
                _timer = null;
                return;
            }
        }

        try
        {
            var stoleFocus = false;
            var dialogShown = false;
            var fg = NativeMethods.GetForegroundWindow();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!IsSteamWindow(hwnd, out var title) || title.Length == 0) return true;
                var visible = NativeMethods.IsWindowVisible(hwnd);

                if (IsMainWindow(hwnd, title))
                {
                    if (hideMain && visible)
                    {
                        if (hwnd == fg) stoleFocus = true;
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                        Log.Info("Ventana principal de Steam ocultada (segundo plano).");
                    }
                    return true;
                }

                if (!surface || !IsUserDialog(hwnd, title, visible)) return true;
                Interlocked.Exchange(ref _lastDialogTicks, DateTime.UtcNow.Ticks);
                bool first;
                lock (Gate) first = Surfaced.Add(hwnd);
                if (!first && visible) return true;

                // Diálogo escondido con su ventana dueña (minimizada u oculta) o recién aparecido: flotante y al frente.
                if (!visible || NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNORMAL);
                BringToFront(hwnd);
                dialogShown = true;
                Log.Info($"Diálogo de Steam mostrado para el usuario: «{title}».");
                return true;
            }, IntPtr.Zero);

            if (stoleFocus && !dialogShown && _userForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(_userForeground);
        }
        catch (Exception ex)
        {
            Log.Warn($"Guardia de ventanas de Steam: {ex.Message}");
        }
    }

    /// <summary>La ventana principal: título «Steam» y tamaño de ventana completa (los diálogos son pequeños).</summary>
    private static bool IsMainWindow(IntPtr hwnd, string title)
    {
        if (!title.Equals("Steam", StringComparison.Ordinal)) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
        var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
        // Minimizada (-minimized) mide 160×28 o similar: también es la principal si no tiene dueña.
        if (NativeMethods.IsIconic(hwnd)) return NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == IntPtr.Zero;
        return w >= 760 && h >= 480;
    }

    /// <summary>
    /// ¿Es un diálogo que espera al usuario? Ventana de primer nivel de la interfaz de Steam, no de las que se
    /// ignoran, de tamaño de diálogo. Si está visible basta con eso; si Steam la tiene escondida, solo se muestra
    /// si su título es de los diálogos previos al juego (así nunca se destapan ventanas internas de Steam).
    /// </summary>
    private static bool IsUserDialog(IntPtr hwnd, string title, bool visible)
    {
        var lower = title.ToLowerInvariant();
        if (IgnoredTitles.Any(lower.Contains)) return false;
        var cls = ClassName(hwnd);
        if (!DialogClasses.Contains(cls, StringComparer.Ordinal)) return false;
        if (NativeMethods.GetWindowRect(hwnd, out var r))
        {
            var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
            if (!NativeMethods.IsIconic(hwnd) && (w < 200 || h < 100 || w > 2400 || h > 1600)) return false;
        }
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & NativeMethods.WS_EX_APPWINDOW) == 0 && !Matches(lower))
            return false; // notificaciones y ventanas auxiliares
        // Visible (incluidos los diálogos pequeños titulados «Steam»): se trae al frente. Escondida: solo si el título
        // es de un diálogo previo al juego, para no destapar ventanas internas de Steam.
        return visible || Matches(lower);
    }

    private static bool Matches(string lowerTitle) => DialogKeywords.Any(lowerTitle.Contains);

    /// <summary>Trae una ventana al frente aunque Windows limite el cambio de foco (se une al hilo del que lo tiene).</summary>
    private static void BringToFront(IntPtr hwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        var fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        var me = NativeMethods.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && NativeMethods.AttachThreadInput(me, fgThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(me, fgThread, false);
        }
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
