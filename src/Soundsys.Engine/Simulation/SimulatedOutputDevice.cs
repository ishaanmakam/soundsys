using Soundsys.Engine.Abstractions;

namespace Soundsys.Engine.Simulation;

/// <summary>Configuration of a simulated Bluetooth speaker.</summary>
public sealed record SimSpeakerOptions
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsBluetooth { get; init; } = true;
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 2;

    /// <summary>DAC clock error relative to the laptop clock, parts per million.</summary>
    public double Ppm { get; init; }

    /// <summary>Render period (WASAPI shared-mode default ≈ 10 ms).</summary>
    public int PeriodFrames { get; init; } = 480;

    /// <summary>Endpoint buffer size.</summary>
    public int BufferFrames { get; init; } = 1440;

    /// <summary>Random lateness of each callback (0..JitterMs).</summary>
    public double CallbackJitterMs { get; init; } = 1.0;

    /// <summary>Everything after the endpoint buffer: Bluetooth link + speaker DSP + air (what calibration measures).</summary>
    public double AcousticLatencyMs { get; init; } = 200;

    /// <summary>Loudness of this speaker at the simulated laptop mic.</summary>
    public double GainAtMic { get; init; } = 0.5;

    /// <summary>Windows (start, duration seconds, relative to Start) during which callbacks stop (Bluetooth stall).</summary>
    public IReadOnlyList<(double Start, double Duration)> Stalls { get; init; } = [];

    public int Seed { get; init; } = 11;
}

/// <summary>Receives every block a simulated speaker plays (simulated room, test probes).</summary>
public interface ISimPlaybackObserver
{
    /// <param name="device">The speaker.</param>
    /// <param name="dacTime">Laptop-clock time at which the first frame leaves the endpoint buffer.</param>
    /// <param name="trueRate">The speaker's actual frame rate (nominal rate including ppm error).</param>
    /// <param name="interleaved">The frames, interleaved with the device's channel count.</param>
    void OnPlayed(SimulatedOutputDevice device, double dacTime, double trueRate, ReadOnlySpan<float> interleaved);
}

/// <summary>
/// Simulated render endpoint. Its DAC consumes frames continuously at <c>rate·(1+ppm)</c>; callbacks arrive
/// every period (plus jitter) and ask for <c>buffer − padding</c> frames exactly like WASAPI shared event mode.
/// If a callback is too late the buffer runs dry and a glitch is counted. Audio goes only to in-memory observers.
/// </summary>
public sealed class SimulatedOutputDevice : IOutputDevice
{
    private readonly SimScheduler _scheduler;
    private readonly float[] _buffer;
    private readonly Random _rng;
    private readonly List<ISimPlaybackObserver> _observers = [];
    private int _generation;

    public SimulatedOutputDevice(SimScheduler scheduler, SimSpeakerOptions options)
    {
        _scheduler = scheduler;
        Options = options;
        _buffer = new float[options.BufferFrames * options.Channels];
        _rng = new Random(options.Seed);
    }

    public SimSpeakerOptions Options { get; }

    public string Id => Options.Id;

    public string DisplayName => Options.Name;

    public bool IsRunning { get; private set; }

    /// <summary>Times the endpoint buffer ran dry (audible glitch on a real speaker).</summary>
    public long Glitches { get; private set; }

    public long Callbacks { get; private set; }

    public event EventHandler<DeviceFaultEventArgs>? Faulted;

    public void AddObserver(ISimPlaybackObserver observer) => _observers.Add(observer);

    public void Start(IRenderTarget target)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        var generation = ++_generation;
        var format = new AudioFormat(Options.SampleRate, Options.Channels);
        target.Prepare(format, Options.BufferFrames);

        var t0 = _scheduler.Now;
        var trueRate = Options.SampleRate * (1 + (Options.Ppm * 1e-6));
        long written = 0;
        long period = 0;

        void Callback(double now)
        {
            if (generation != _generation)
            {
                return;
            }

            var consumed = (long)Math.Floor((now - t0) * trueRate);
            if (consumed > written)
            {
                if (written > 0)
                {
                    Glitches++;
                }

                written = consumed; // the DAC played silence while starved
            }

            var padding = (int)(written - consumed);
            var frames = Options.BufferFrames - padding;
            if (frames > 0)
            {
                var span = _buffer.AsSpan(0, frames * Options.Channels);
                target.Render(span, frames, new RenderTiming(now, padding));
                var dacTime = t0 + (written / trueRate);
                foreach (var o in _observers)
                {
                    o.OnPlayed(this, dacTime, trueRate, span);
                }

                written += frames;
            }

            Callbacks++;
            period++;
            var next = t0 + (period * Options.PeriodFrames / trueRate);
            foreach (var (start, duration) in Options.Stalls)
            {
                var rel = next - t0;
                if (rel >= start && rel < start + duration)
                {
                    period = (long)Math.Ceiling((start + duration) * trueRate / Options.PeriodFrames);
                    next = t0 + (period * Options.PeriodFrames / trueRate);
                }
            }

            _scheduler.Schedule(next + (_rng.NextDouble() * Options.CallbackJitterMs * 0.001), Callback);
        }

        _scheduler.Schedule(t0, Callback);
    }

    public void Stop()
    {
        IsRunning = false;
        _generation++;
    }

    /// <summary>Simulates the speaker switching off / going out of range: stops and raises <see cref="Faulted"/>.</summary>
    public void SimulateDisconnect()
    {
        if (!IsRunning)
        {
            return;
        }

        Stop();
        Faulted?.Invoke(this, new DeviceFaultEventArgs(DeviceFaultKind.Disconnected, $"{DisplayName} disconnected (simulated)"));
    }

    public void Dispose() => Stop();
}
