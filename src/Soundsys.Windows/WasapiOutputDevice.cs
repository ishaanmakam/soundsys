using System.Globalization;
using Soundsys.Engine.Abstractions;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

/// <summary>
/// Shared-mode, event-driven WASAPI render stream on its own MMCSS "Pro Audio" thread.
/// Requests 32-bit float stereo at the endpoint's mix rate (AUTOCONVERTPCM), so the engine does all resampling.
/// The render loop writes straight into the WASAPI buffer and never allocates.
/// </summary>
public sealed class WasapiOutputDevice : IOutputDevice
{
    /// <summary>Default shared-mode buffer request (30 ms). WASAPI may round it up.</summary>
    public const long DefaultBufferHns = 300_000;

    private const uint EventTimeoutMs = 2000;
    private const int MaxConsecutiveTimeouts = 3;

    private readonly long _bufferHns;
    private readonly object _gate = new();
    private nint _stopEvent;
    private nint _audioEvent;
    private Thread? _thread;
    private StartHandshake? _handshake;
    private IRenderTarget? _target;
    private AudioFormat _format;
    private uint _bufferFrames;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <exception cref="AudioDeviceException">If the endpoint doesn't exist or isn't connected.</exception>
    public WasapiOutputDevice(string endpointId, string displayName, long bufferHns = DefaultBufferHns)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        Id = endpointId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "The speaker" : displayName;
        _bufferHns = bufferHns;

        // Cheap presence check: activate and immediately release (no stream is created).
        var hr = WasapiClient.TryActivate(endpointId, EDataFlow.Render, out var probe, out _);
        WasapiClient.Release(probe);
        if (hr < 0)
            throw AudioErrors.ToException(hr, DisplayName, "Activate IAudioClient");

        _stopEvent = AudioThreading.CreateManualResetEvent();
        _audioEvent = AudioThreading.CreateAutoResetEvent();
    }

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>The negotiated device format (valid after <see cref="Start"/>).</summary>
    public AudioFormat Format => _format;

    /// <summary>The WASAPI buffer size in frames (valid after <see cref="Start"/>).</summary>
    public int BufferFrames => (int)_bufferFrames;

    /// <summary>Managed bytes allocated by the render thread inside the loop during the last run (should be 0).</summary>
    public long LoopAllocatedBytes { get; private set; }

    public event EventHandler<DeviceFaultEventArgs>? Faulted;

    public void Start(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Thread thread;
        StartHandshake handshake;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true })
                throw new InvalidOperationException("The output device is already running.");
            NativeMethods.ResetEvent(_stopEvent);
            _stopRequested = false;
            _target = target;
            handshake = new StartHandshake();
            _handshake = handshake;
            thread = new Thread(RenderThread)
            {
                IsBackground = true,
                Name = "soundsys render",
                Priority = ThreadPriority.Highest,
            };
            thread.SetApartmentState(ApartmentState.MTA);
            _thread = thread;
        }

        thread.Start();
        try
        {
            handshake.WaitOpened();
            target.Prepare(_format, (int)_bufferFrames);
        }
        catch
        {
            handshake.Cancel();
            thread.Join();
            throw;
        }

        handshake.Proceed();
        try
        {
            handshake.WaitStarted();
        }
        catch
        {
            thread.Join();
            throw;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            if (thread is null)
                return;
            _stopRequested = true;
            NativeMethods.SetEvent(_stopEvent);
        }

        if (thread != Thread.CurrentThread)
            thread.Join();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        Stop();
        lock (_gate)
        {
            _disposed = true;
            AudioThreading.CloseHandle(ref _stopEvent);
            AudioThreading.CloseHandle(ref _audioEvent);
        }
    }

    private void RenderThread()
    {
        var handshake = _handshake!;
        var target = _target!;
        WasapiClient? client = null;
        IAudioRenderClientNative? renderClient = null;
        nint mmcss = 0;
        var started = false;
        var faultHr = 0;
        Exception? faultException = null;
        try
        {
            var hr = WasapiClient.TryOpenEndpoint(Id, EDataFlow.Render, AudioClientStreamFlags.EventCallback, _bufferHns, false, out client, out _);
            if (hr < 0)
            {
                handshake.ReportFailure(AudioErrors.ToException(hr, DisplayName, "Open render stream"));
                return;
            }

            if (client!.Format.Kind != SampleKind.Float32)
            {
                handshake.ReportFailure(AudioErrors.ToException(AudioErrors.AudclntEUnsupportedFormat, DisplayName, $"Render format {client.Format}"));
                return;
            }

            hr = client.Client.SetEventHandle(_audioEvent);
            if (hr < 0)
            {
                handshake.ReportFailure(AudioErrors.ToException(hr, DisplayName, "SetEventHandle"));
                return;
            }

            renderClient = client.GetService<IAudioRenderClientNative>(CoreAudioIids.AudioRenderClient);
            _format = new AudioFormat(client.Format.SampleRate, client.Format.Channels);
            _bufferFrames = client.BufferFrames;
            handshake.ReportOpened();
            if (!handshake.WaitProceed())
                return;

            hr = PrefillSilence(renderClient, client.BufferFrames);
            if (hr >= 0)
                hr = client.Client.Start();
            if (hr < 0)
            {
                handshake.ReportFailure(AudioErrors.ToException(hr, DisplayName, "Start render stream"));
                return;
            }

            started = true;
            mmcss = AudioThreading.EnterProAudio();
            handshake.ReportStarted();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            faultHr = RenderLoop(client.Client, renderClient, target, client.BufferFrames, client.Format.Channels, out faultException);
            LoopAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }
        catch (Exception ex)
        {
            if (!started)
            {
                handshake.ReportFailure(new AudioDeviceException(AudioErrors.ToUserMessage(ex.HResult, DisplayName), ex));
                return;
            }

            faultHr = ex.HResult < 0 ? ex.HResult : AudioErrors.EUnexpected;
            faultException = ex;
        }
        finally
        {
            AudioThreading.LeaveProAudio(mmcss);
            if (started)
                client?.Client.Stop();
            WasapiClient.Release(renderClient);
            client?.Dispose();
        }

        if (faultHr != 0 && !_stopRequested)
            RaiseFault(faultHr, faultException);
    }

    private static unsafe int PrefillSilence(IAudioRenderClientNative renderClient, uint frames)
    {
        var hr = renderClient.GetBuffer(frames, out _);
        return hr < 0 ? hr : renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
    }

    /// <summary>The real-time loop. Returns 0 on a requested stop, else the failing HRESULT. No allocation.</summary>
    private unsafe int RenderLoop(IAudioClientNative audioClient, IAudioRenderClientNative renderClient, IRenderTarget target, uint bufferFrames, int channels, out Exception? targetError)
    {
        targetError = null;
        var handles = stackalloc nint[2];
        handles[0] = _stopEvent;
        handles[1] = _audioEvent;
        var timeouts = 0;
        while (true)
        {
            var wait = NativeMethods.WaitForMultipleObjects(2, handles, false, EventTimeoutMs);
            if (wait == NativeMethods.WaitObject0)
                return 0;

            int hr;
            if (wait == NativeMethods.WaitTimeout)
            {
                // No buffer events: either the device died or the audio engine stalled (e.g. Bluetooth link loss).
                hr = audioClient.GetCurrentPadding(out _);
                if (hr < 0)
                    return hr;
                if (++timeouts >= MaxConsecutiveTimeouts)
                    return AudioErrors.AudclntEDeviceInvalidated;
                continue;
            }

            if (wait != NativeMethods.WaitObject0 + 1)
                return AudioErrors.EUnexpected;
            timeouts = 0;

            hr = audioClient.GetCurrentPadding(out var padding);
            if (hr < 0)
                return hr;
            var frames = bufferFrames - padding;
            if (frames == 0)
                continue;

            hr = renderClient.GetBuffer(frames, out var data);
            if (hr < 0)
                return hr;

            var span = new Span<float>(data, (int)frames * channels);
            try
            {
                target.Render(span, (int)frames, new RenderTiming(QpcClock.Now, (int)padding));
            }
            catch (Exception ex)
            {
                renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
                targetError = ex;
                return AudioErrors.EUnexpected;
            }

            hr = renderClient.ReleaseBuffer(frames, 0);
            if (hr < 0)
                return hr;
        }
    }

    private void RaiseFault(int hr, Exception? error)
    {
        var kind = error is null ? AudioErrors.ToFaultKind(hr) : DeviceFaultKind.Failed;
        var detail = error is null
            ? string.Create(CultureInfo.InvariantCulture, $"Render stream for '{DisplayName}' ({Id}) stopped: {AudioErrors.Describe(hr)}")
            : $"Render stream for '{DisplayName}' ({Id}) stopped: {error.GetType().Name}: {error.Message}";
        var args = new DeviceFaultEventArgs(kind, detail);
        lock (_gate)
        {
            _thread = null;
        }

        // Raised from the thread pool so handlers may call Stop()/Dispose() or block safely.
        ThreadPool.QueueUserWorkItem(static s => s.Self.Faulted?.Invoke(s.Self, s.Args), (Self: this, Args: args), preferLocal: false);
    }
}
