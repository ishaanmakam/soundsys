using System.Diagnostics;

namespace Soundsys.App.Hosting;

/// <summary>
/// One soundsys per user session: a named mutex. A second launch signals the first (named event) to show its window,
/// then exits. A restarted instance waits for the old one to let go.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\soundsys.single-instance";
    public const string ShowEventName = @"Local\soundsys.show-window";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    /// <summary>Returns the instance if this process is the first; otherwise asks the first to show itself and returns null.</summary>
    public static SingleInstance? TryAcquire(bool waitForPrevious)
    {
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(45) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous instance crashed; we own it now
        }

        if (owned)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        SignalExisting();
        return null;
    }

    /// <summary>Calls <paramref name="onShow"/> (thread pool) whenever another launch asks to show the window.</summary>
    public void ListenForShow(Action onShow) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Unregister(null);
        _show.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            Trace.TraceWarning($"soundsys: single-instance mutex release: {ex.Message}");
        }

        _mutex.Dispose();
    }

    private static void SignalExisting()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(ShowEventName);
            show.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"soundsys: couldn't signal the running instance: {ex.Message}");
        }
    }
}
