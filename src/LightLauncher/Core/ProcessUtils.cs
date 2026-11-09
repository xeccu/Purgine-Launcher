using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using LightLauncher.Infrastructure;

namespace LightLauncher.Core;

/// <summary>Utilidades de bajo nivel sobre procesos del sistema operativo.</summary>
public static class ProcessUtils
{
    /// <summary>
    /// Obtiene la ruta completa del ejecutable de un proceso sin necesitar privilegios elevados
    /// (más robusto que <c>Process.MainModule</c>, que falla con procesos de 64 bits/protegidos).
    /// </summary>
    public static string? TryGetProcessPath(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Devuelve los PID cuyo ejecutable vive dentro de <paramref name="directory"/>.</summary>
    public static List<int> FindProcessesUnder(string directory)
    {
        var result = new List<int>();
        var prefix = directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var self = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var pid = p.Id;
                if (pid <= 4 || pid == self) continue; // Idle / System / nosotros mismos
                var path = TryGetProcessPath(pid);
                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(pid);
            }
        }
        return result;
    }

    public static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }          // no existe
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }               // existe pero sin acceso
    }

    /// <summary>Mata (con su árbol) todos los procesos con esos nombres. Devuelve cuántos se cerraron.</summary>
    public static int KillByNames(IEnumerable<string> processNames)
    {
        int killed = 0;
        foreach (var name in processNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                        killed++;
                        Log.Info($"Proceso residual cerrado: {name} (PID {p.Id}).");
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Ya terminó, o pertenece a otro usuario / es protegido.
                        Log.Warn($"No se pudo cerrar {name} (PID {p.Id}): {ex.Message}");
                    }
                }
            }
        }
        return killed;
    }

    /// <summary>Arranca un ejecutable sin ventana de consola ni shell.</summary>
    public static void Start(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        using var _ = Process.Start(psi);
    }

    /// <summary>
    /// Compacta el heap gestionado y devuelve al SO las páginas no usadas del working set.
    /// Se llama cuando la ventana se minimiza o termina una sesión: el launcher queda en mínimos.
    /// </summary>
    public static void TrimCurrentProcess()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess();
            NativeMethods.SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch (Exception ex)
        {
            Log.Warn($"TrimCurrentProcess falló: {ex.Message}");
        }
    }

    /// <summary>Espera (sondeando) hasta que se cumpla la condición o venza el plazo.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct, int pollMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }
        return condition();
    }
}
