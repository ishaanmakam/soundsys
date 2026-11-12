using System.Globalization;
using Soundsys.Engine.Abstractions;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

/// <summary>
/// Shared capture thread for endpoint capture, endpoint loopback and process loopback.
/// Opens the stream on its own MTA thread, announces the format synchronously from <see cref="Start"/>,
/// then delivers packets with QPC timestamps. The capture loop is allocation-free.
/// </summary>
public abstract class WasapiCaptureBase : IAudioSource
{
    /// <summary>Capture buffer request (100 ms): only overflow headroom, packets are read as soon as they arrive.</summary>
    protected const long CaptureBufferHns = 1_000_000;

    /// <summary>Polling interval for loopback streams (5 ms), where event callbacks are unreliable on some builds.</summary>
    private const long PollIntervalHns = 50_000;

    private readonly object _gate = new();
    private nint _stopEvent;
    private nint _audioEvent;
    private nint _pollTimer;
    private Thread? _thread;
    private StartHandshake? _handshake;
    private ISourceSink? _sink;
    private AudioFormat _format;
    private volatile bool _stopRequested;
    private long _packetCount;
    private bool _disposed;

    protected WasapiCaptureBase(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
        _stopEvent = AudioThreading.CreateManualResetEvent();
        _audioEvent = AudioThreading.CreateAutoResetEvent();
        _pollTimer = AudioThreading.CreatePollTimer();
    }

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>The announced format (valid after <see cref="Start"/>).</summary>
    public AudioFormat Format => _format;

    /// <summary>True when raw mode (no Windows voice processing) was granted for this stream.</summary>
    public bool RawModeActive { get; private set; }

    /// <summary>Packets delivered since start (diagnostics; single writer: the capture thread).</summary>
    public long PacketCount => Interlocked.Read(ref _packetCount);

    public event EventHandler<DeviceFaultEventArgs>? Faulted;

    /// <summary>
    /// Opens and initializes the stream on the capture thread.
    /// </summary>
    /// <param name="client">The initialized client.</param>
    /// <param name="eventDriven">True to wait on the buffer event; false to poll every 5 ms.</param>
    /// <returns>HRESULT; failures become an <see cref="AudioDeviceException"/> via <see cref="CreateStartException"/>.</returns>
    private protected abstract int OpenStream(out WasapiClient? client, out bool eventDriven);

    /// <summary>Maps an open failure to a plain-English exception (override for source-specific wording).</summary>
    protected virtual AudioDeviceException CreateStartException(int hr) => AudioErrors.ToException(hr, DisplayName, "Open capture stream", isCapture: true);

    public void Start(ISourceSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Thread thread;
        StartHandshake handshake;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true })
                throw new InvalidOperationException("The source is already running.");
            NativeMethods.ResetEvent(_stopEvent);
            _stopRequested = false;
            _sink = sink;
            Interlocked.Exchange(ref _packetCount, 0);
            handshake = new StartHandshake();
            _handshake = handshake;
            thread = new Thread(CaptureThread)
            {
                IsBackground = true,
                Name = "soundsys capture",
                Priority = ThreadPriority.Highest,
            };
            thread.SetApartmentState(ApartmentState.MTA);
            _thread = thread;
        }

        thread.Start();
        try
        {
            handshake.WaitOpened();
            sink.OnFormat(_format);
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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
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
            AudioThreading.CloseHandle(ref _pollTimer);
        }
    }

    private void CaptureThread()
    {
        var handshake = _handshake!;
        var sink = _sink!;
        WasapiClient? client = null;
        IAudioCaptureClientNative? captureClient = null;
        nint mmcss = 0;
        var started = false;
        var faultHr = 0;
        Exception? faultException = null;
        try
        {
            var hr = OpenStream(out client, out var eventDriven);
            if (hr < 0)
            {
                handshake.ReportFailure(CreateStartException(hr));
                return;
            }

            if (client!.Format.Kind == SampleKind.Unsupported)
            {
                handshake.ReportFailure(CreateStartException(AudioErrors.AudclntEUnsupportedFormat));
                return;
            }

            if (eventDriven)
            {
                hr = client.Client.SetEventHandle(_audioEvent);
                if (hr < 0)
                {
                    handshake.ReportFailure(CreateStartException(hr));
                    return;
                }
            }

            captureClient = client.GetService<IAudioCaptureClientNative>(CoreAudioIids.AudioCaptureClient);
            _format = new AudioFormat(client.Format.SampleRate, client.Format.Channels);
            RawModeActive = client.RawMode;
            var scratch = new float[Math.Max(1, (int)client.BufferFrames) * client.Format.Channels];
            handshake.ReportOpened();
            if (!handshake.WaitProceed())
                return;

            hr = client.Client.Start();
            if (hr < 0)
            {
                handshake.ReportFailure(CreateStartException(hr));
                return;
            }

            started = true;
            mmcss = AudioThreading.EnterProAudio();
            handshake.ReportStarted();
            faultHr = CaptureLoop(client.Client, captureClient, sink, client.Format, eventDriven, scratch, out faultException);
        }
        catch (Exception ex)
        {
            if (!started)
            {
                handshake.ReportFailure(ex as AudioDeviceException ?? new AudioDeviceException(AudioErrors.ToUserMessage(ex.HResult, DisplayName, isCapture: true), ex));
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
            WasapiClient.Release(captureClient);
            client?.Dispose();
        }

        if (faultHr != 0 && !_stopRequested)
            RaiseFault(faultHr, faultException);
    }

    /// <summary>Real-time capture loop. Returns 0 on requested stop, else the failing HRESULT. No allocation.</summary>
    private unsafe int CaptureLoop(IAudioClientNative audioClient, IAudioCaptureClientNative captureClient, ISourceSink sink, StreamFormat format, bool eventDriven, float[] scratch, out Exception? sinkError)
    {
        sinkError = null;
        var handles = stackalloc nint[2];
        handles[0] = _stopEvent;
        handles[1] = eventDriven ? _audioEvent : _pollTimer;
        var channels = format.Channels;
        var secondsPerFrame = 1.0 / format.SampleRate;
        var scratchFrames = scratch.Length / channels;
        var directFloat = format.Kind == SampleKind.Float32;

        while (true)
        {
            if (!eventDriven)
                AudioThreading.ArmTimer(_pollTimer, PollIntervalHns);
            var wait = NativeMethods.WaitForMultipleObjects(2, handles, false, 2000);
            if (wait == NativeMethods.WaitObject0)
                return 0;
            if (wait == NativeMethods.WaitTimeout)
            {
                // Silence on loopback / process loopback produces no packets; only a failing call is fatal.
                var check = audioClient.GetCurrentPadding(out _);
                if (check < 0)
                    return check;
                continue;
            }

            if (wait != NativeMethods.WaitObject0 + 1)
                return AudioErrors.EUnexpected;

            while (true)
            {
                var hr = captureClient.GetNextPacketSize(out var next);
                if (hr < 0)
                    return hr;
                if (next == 0)
                    break;

                hr = captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc);
                if (hr == AudioErrors.AudclntSBufferEmpty)
                    break;
                if (hr < 0)
                    return hr;

                var timestamp = (flags & AudioClientBufferFlags.TimestampError) != 0 || qpc == 0
                    ? QpcClock.Now - (frames * secondsPerFrame)
                    : QpcClock.FromWasapiQpc(qpc);
                var discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
                var silent = (flags & AudioClientBufferFlags.Silent) != 0;

                try
                {
                    if (directFloat && !silent)
                    {
                        sink.Write(new ReadOnlySpan<float>(data, (int)frames * channels), timestamp, discontinuity);
                    }
                    else
                    {
                        var offset = 0;
                        while (offset < frames)
                        {
                            var chunk = Math.Min(scratchFrames, (int)frames - offset);
                            var span = scratch.AsSpan(0, chunk * channels);
                            if (silent)
                                span.Clear();
                            else
                                FormatNegotiation.ToFloat(format.Kind, data + ((long)offset * format.BlockAlign), span);
                            sink.Write(span, timestamp + (offset * secondsPerFrame), discontinuity && offset == 0);
                            offset += chunk;
                        }
                    }
                }
                catch (Exception ex)
                {
                    captureClient.ReleaseBuffer(frames);
                    sinkError = ex;
                    return AudioErrors.EUnexpected;
                }

                hr = captureClient.ReleaseBuffer(frames);
                if (hr < 0)
                    return hr;
                _packetCount++;
            }
        }
    }

    private void RaiseFault(int hr, Exception? error)
    {
        var kind = error is null ? AudioErrors.ToFaultKind(hr) : DeviceFaultKind.Failed;
        var detail = error is null
            ? string.Create(CultureInfo.InvariantCulture, $"Capture stream '{DisplayName}' ({Id}) stopped: {AudioErrors.Describe(hr)}")
            : $"Capture stream '{DisplayName}' ({Id}) stopped: {error.GetType().Name}: {error.Message}";
        var args = new DeviceFaultEventArgs(kind, detail);
        lock (_gate)
        {
            _thread = null;
        }

        ThreadPool.QueueUserWorkItem(static s => s.Self.Faulted?.Invoke(s.Self, s.Args), (Self: this, Args: args), preferLocal: false);
    }
}
