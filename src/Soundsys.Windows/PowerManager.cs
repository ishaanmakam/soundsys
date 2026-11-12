using Soundsys.Engine.Abstractions;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

/// <summary>
/// Keeps the laptop from sleeping while soundsys is active. SetThreadExecutionState is per-thread (the request ends when
/// the calling thread exits), so a dedicated long-lived background thread owns the request.
/// The display may still turn off: only ES_SYSTEM_REQUIRED is requested.
/// </summary>
public sealed class PowerManager : IPowerManager, IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _desired;
    private volatile bool _exit;

    public PowerManager()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "soundsys keep-awake" };
        _thread.Start();
    }

    /// <summary>True while the keep-awake request is in force (diagnostics).</summary>
    public bool IsKeepingAwake { get; private set; }

    public void SetKeepAwake(bool keepAwake)
    {
        _desired = keepAwake;
        _signal.Set();
    }

    private void Run()
    {
        while (true)
        {
            _signal.WaitOne();
            if (_exit)
            {
                NativeMethods.SetThreadExecutionState(NativeMethods.EsContinuous);
                IsKeepingAwake = false;
                return;
            }

            var flags = NativeMethods.EsContinuous | (_desired ? NativeMethods.EsSystemRequired : 0);
            IsKeepingAwake = NativeMethods.SetThreadExecutionState(flags) != 0 && _desired;
        }
    }

    public void Dispose()
    {
        if (_exit)
            return;
        _exit = true;
        _signal.Set();
        _thread.Join();
        _signal.Dispose();
    }
}
