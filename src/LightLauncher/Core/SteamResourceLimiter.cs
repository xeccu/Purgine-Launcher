using System.ComponentModel;
using System.Diagnostics;
using LightLauncher.Infrastructure;
using LightLauncher.Settings;

namespace LightLauncher.Core;

/// <summary>Resultado de una pasada del limitador.</summary>
/// <param name="Processes">Procesos de Steam encontrados.</param>
/// <param name="Reprioritized">steamwebhelper puestos en la prioridad configurada.</param>
/// <param name="Killed">steamwebhelper cerrados (solo con la opción experimental).</param>
/// <param name="WorkingSetBefore">RAM física (working set) total antes, en bytes.</param>
/// <param name="WorkingSetAfter">RAM física total después, en bytes.</param>
public sealed record SteamLimitResult(int Processes, int Reprioritized, int Killed, long WorkingSetBefore, long WorkingSetAfter)
{
    public string Describe() =>
        Processes == 0
            ? "Steam no está en ejecución."
            : $"Steam limitado: {Reprioritized} procesos web en prioridad baja{(Killed > 0 ? $", {Killed} cerrados" : string.Empty)}, " +
              $"RAM {WorkingSetBefore / 1048576:N0} MB → {WorkingSetAfter / 1048576:N0} MB";
}

/// <summary>
/// Reduce lo que Steam consume <b>mientras se juega</b>, sin romper la validación de licencias:
/// <list type="bullet">
///   <item><c>steamwebhelper</c> (la interfaz Chromium de Steam, la mayor parte de su RAM) pasa a prioridad
///         de CPU baja, para que no compita con el juego.</item>
///   <item>Se recorta el working set de <c>steam.exe</c> y <c>steamwebhelper</c>: Windows saca de la RAM física
///         las páginas que no se están usando. No mata nada; si Steam las necesita, vuelven solas.</item>
///   <item>Opcional (experimental): cerrar <c>steamwebhelper</c>. Se pierde overlay y notificaciones.</item>
/// </list>
/// <c>steam.exe</c> conserva su prioridad normal: lo necesitan el DRM y la comunicación con el juego.
/// </summary>
public static class SteamResourceLimiter
{
    private const string WebHelper = "steamwebhelper";
    private const string Client = "steam";

    /// <summary>Aplica la limitación según los ajustes. Seguro de llamar repetidamente.</summary>
    public static SteamLimitResult Apply(LauncherSettings settings)
    {
        var helpers = Process.GetProcessesByName(WebHelper);
        var clients = Process.GetProcessesByName(Client);
        try
        {
            var all = helpers.Concat(clients).ToArray();
            long before = SumWorkingSet(all);
            int reprioritized = 0, killed = 0;

            if (settings.KillSteamWebHelperWhilePlaying)
            {
                foreach (var p in helpers)
                    if (TryKill(p)) killed++;
            }
            else
            {
                var priority = ToPriorityClass(settings.SteamHelperPriority);
                foreach (var p in helpers)
                    if (TrySetPriority(p, priority)) reprioritized++;
            }

            if (settings.TrimSteamMemoryWhilePlaying)
            {
                foreach (var p in all)
                    TrimWorkingSet(p.Id);
            }

            long after = SumWorkingSet(all);
            var result = new SteamLimitResult(all.Length, reprioritized, killed, before, after);
            if (all.Length > 0) Log.Info(result.Describe());
            return result;
        }
        finally
        {
            foreach (var p in helpers) p.Dispose();
            foreach (var p in clients) p.Dispose();
        }
    }

    /// <summary>Devuelve steamwebhelper a prioridad normal (si Steam se queda abierto tras la partida).</summary>
    public static void RestorePriority()
    {
        foreach (var p in Process.GetProcessesByName(WebHelper))
        {
            using (p) TrySetPriority(p, ProcessPriorityClass.Normal);
        }
    }

    /// <summary>Pone un proceso en la prioridad indicada. False si ya terminó o no hay permiso.</summary>
    public static bool TrySetPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            if (process.HasExited) return false;
            if (process.PriorityClass != priority) process.PriorityClass = priority;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pide a Windows que saque de la RAM física las páginas no usadas del proceso
    /// (equivalente a <c>EmptyWorkingSet</c>). No cierra ni pausa el proceso.
    /// </summary>
    public static bool TrimWorkingSet(int pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return NativeMethods.SetProcessWorkingSetSize(handle, -1, -1);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static long SumWorkingSet(IEnumerable<Process> processes)
    {
        long total = 0;
        foreach (var p in processes)
        {
            try
            {
                p.Refresh();
                if (!p.HasExited) total += p.WorkingSet64;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { /* terminó o sin acceso */ }
        }
        return total;
    }

    private static ProcessPriorityClass ToPriorityClass(HelperPriority p) => p switch
    {
        HelperPriority.Idle => ProcessPriorityClass.Idle,
        HelperPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        _ => ProcessPriorityClass.Normal,
    };
}
