namespace Purgine.Infrastructure;

/// <summary>
/// Ejecuta una acción una sola vez tras un periodo de calma (p. ej. guardar ajustes mientras se
/// arrastra un slider, sin escribir el archivo decenas de veces por segundo).
/// </summary>
public sealed class DebouncedAction : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private int _pending;

    public DebouncedAction(Action action, TimeSpan delay)
    {
        _action = action;
        _delay = delay;
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Request()
    {
        Interlocked.Exchange(ref _pending, 1);
        _timer.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Ejecuta ya lo pendiente (al cerrar la app).</summary>
    public void Flush()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Run();
    }

    private void Run()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;
        try { _action(); }
        catch (Exception ex) { Log.Error("Acción diferida fallida", ex); }
    }

    public void Dispose() => _timer.Dispose();
}
