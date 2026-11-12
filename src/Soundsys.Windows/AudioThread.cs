using System.Runtime.InteropServices;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

/// <summary>
/// Start handshake between a caller thread and a dedicated (MTA) audio thread:
/// the audio thread opens the device and reports, the caller does its non-RT preparation
/// (Prepare/OnFormat) and lets it proceed, then the audio thread starts the stream and reports again.
/// Not disposed: the slim events never allocate kernel handles here, and the audio thread may still be
/// leaving Set() when the caller wakes.
/// </summary>
internal sealed class StartHandshake
{
    private readonly ManualResetEventSlim _opened = new();
    private readonly ManualResetEventSlim _proceed = new();
    private readonly ManualResetEventSlim _started = new();
    private Exception? _error;
    private volatile bool _cancelled;

    public bool Cancelled => _cancelled;

    // Audio-thread side.
    public void ReportOpened() => _opened.Set();

    /// <summary>Blocks the audio thread until the caller says go; false if the caller cancelled.</summary>
    public bool WaitProceed()
    {
        _proceed.Wait();
        return !_cancelled;
    }

    public void ReportStarted() => _started.Set();

    public void ReportFailure(Exception error)
    {
        _error = error;
        _opened.Set();
        _started.Set();
    }

    // Caller side.
    public void WaitOpened()
    {
        _opened.Wait();
        if (_error is not null)
            throw _error;
    }

    public void Proceed() => _proceed.Set();

    public void Cancel()
    {
        _cancelled = true;
        _proceed.Set();
    }

    public void WaitStarted()
    {
        _started.Wait();
        if (_error is not null)
            throw _error;
    }
}

/// <summary>Win32 event / MMCSS helpers for the audio threads.</summary>
internal static class AudioThreading
{
    public static nint CreateAutoResetEvent()
    {
        var h = NativeMethods.CreateEvent(0, false, false, 0);
        if (h == 0)
            throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()}).");
        return h;
    }

    public static nint CreateManualResetEvent()
    {
        var h = NativeMethods.CreateEvent(0, true, false, 0);
        if (h == 0)
            throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()}).");
        return h;
    }

    /// <summary>A high-resolution waitable timer (falls back to a normal one on older builds).</summary>
    public static nint CreatePollTimer()
    {
        var h = NativeMethods.CreateWaitableTimerEx(0, 0, NativeMethods.CreateWaitableTimerHighResolution, NativeMethods.TimerAllAccess);
        if (h == 0)
            h = NativeMethods.CreateWaitableTimerEx(0, 0, 0, NativeMethods.TimerAllAccess);
        if (h == 0)
            throw new InvalidOperationException($"CreateWaitableTimerEx failed ({Marshal.GetLastPInvokeError()}).");
        return h;
    }

    /// <summary>Arms a one-shot relative timer (allocation-free).</summary>
    public static unsafe void ArmTimer(nint timer, long relative100ns)
    {
        var due = -relative100ns;
        NativeMethods.SetWaitableTimer(timer, &due, 0, 0, 0, false);
    }

    public static void CloseHandle(ref nint handle)
    {
        if (handle != 0)
        {
            NativeMethods.CloseHandle(handle);
            handle = 0;
        }
    }

    /// <summary>Registers the current thread with MMCSS "Pro Audio". Returns 0 if unavailable.</summary>
    public static nint EnterProAudio()
    {
        uint index = 0;
        try
        {
            return NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref index);
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    public static void LeaveProAudio(nint handle)
    {
        if (handle != 0)
            NativeMethods.AvRevertMmThreadCharacteristics(handle);
    }
}
