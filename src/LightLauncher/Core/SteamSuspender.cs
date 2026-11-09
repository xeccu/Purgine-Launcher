using System.Diagnostics;
using LightLauncher.Infrastructure;

namespace LightLauncher.Core;

/// <summary>
/// Pausa / reanuda las descargas de Steam sin abrir su ventana, congelando sus procesos
/// (<c>NtSuspendProcess</c>). Steam no ofrece ningún comando para pausar una descarga concreta, así que
/// esta pausa es global: mientras dura, Steam entero está detenido (descargas, amigos, interfaz).
/// <para>Salvaguardas: nunca con un juego de Steam abierto; los PID se guardan en disco para reanudarlos
/// al iniciar LightLauncher si se cerró de golpe; se reanuda al salir y antes de cualquier acción con Steam.</para>
/// </summary>
public sealed class SteamSuspender
{
    private static readonly string[] Targets = ["steam", "steamwebhelper"];
    private readonly string _stateFile;
    private readonly object _gate = new();
    private List<int> _suspended = [];

    public SteamSuspender(string stateFile)
    {
        _stateFile = stateFile;
        // Si la sesión anterior terminó con Steam congelado, se descongela ya.
        var leftover = JsonFileStore.Load<List<int>>(stateFile);
        if (leftover.Count > 0)
        {
            Log.Warn($"Steam quedó pausado en la sesión anterior ({leftover.Count} procesos): reanudando.");
            foreach (var pid in leftover) ResumePid(pid);
        }
        TryDeleteState();
    }

    public bool IsSuspended { get { lock (_gate) return _suspended.Count > 0; } }

    /// <summary>Congela Steam. Devuelve un mensaje de error o null si se pausó.</summary>
    public string? Suspend()
    {
        lock (_gate)
        {
            if (_suspended.Count > 0) return null;
            if (SteamLocator.GetRunningAppId() != 0) return "Hay un juego de Steam abierto: no se puede pausar Steam.";

            var pids = new List<int>();
            foreach (var name in Targets)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        if (SuspendPid(p.Id)) pids.Add(p.Id);
                    }
                }
            }
            if (pids.Count == 0) return "Steam no está en ejecución.";

            _suspended = pids;
            JsonFileStore.Save(_stateFile, _suspended);
            Log.Info($"Descargas pausadas: Steam congelado ({pids.Count} procesos).");
            return null;
        }
    }

    /// <summary>Descongela Steam (no hace nada si no estaba pausado).</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_suspended.Count == 0) return;
            foreach (var pid in _suspended) ResumePid(pid);
            Log.Info($"Descargas reanudadas ({_suspended.Count} procesos).");
            _suspended = [];
            TryDeleteState();
        }
    }

    private static bool SuspendPid(int pid) => WithHandle(pid, h => NativeMethods.NtSuspendProcess(h) == 0);

    private static bool ResumePid(int pid) => WithHandle(pid, h => NativeMethods.NtResumeProcess(h) == 0);

    private static bool WithHandle(int pid, Func<IntPtr, bool> action)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            // Solo procesos de Steam: un PID guardado podría haberse reutilizado.
            var path = ProcessUtils.TryGetProcessPath(pid);
            var name = path is null ? null : Path.GetFileNameWithoutExtension(path);
            if (name is null || !Targets.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            return action(h);
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    private void TryDeleteState()
    {
        try { File.Delete(_stateFile); } catch (Exception) { /* no existe */ }
    }
}
