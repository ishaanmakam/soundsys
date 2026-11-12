using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Dsp;

namespace Soundsys.Engine.Simulation;

/// <summary>Quiet, music-like signal (chord + soft percussion). Never reaches a real speaker.</summary>
public sealed class MusicLikeSignal : ISignal
{
    private static readonly double[] Freqs = [110, 220, 277.18, 329.63, 440, 659.25, 880, 1760];

    public void Fill(Span<float> interleaved, int channels, long firstFrame, int sampleRate)
    {
        var frames = interleaved.Length / channels;
        for (var i = 0; i < frames; i++)
        {
            var n = firstFrame + i;
            var t = n / (double)sampleRate;
            double v = 0;
            for (var k = 0; k < Freqs.Length; k++)
            {
                v += Math.Sin(2 * Math.PI * Freqs[k] * t) / (k + 2);
            }

            var beat = t % 0.5;
            if (beat < 0.04)
            {
                // Deterministic pseudo-noise burst.
                var h = (uint)(n * 2654435761u);
                v += (((h >> 8) & 0xFFFF) / 32768.0 - 1.0) * (1 - (beat / 0.04));
            }

            var s = (float)(0.2 * v);
            for (var c = 0; c < channels; c++)
            {
                interleaved[(i * channels) + c] = c == 1 ? s * 0.9f : s;
            }
        }
    }
}

/// <summary>Configuration of a <see cref="SimulatedSource"/>.</summary>
public sealed record SimSourceOptions
{
    public string Id { get; init; } = "sim-source";
    public string Name { get; init; } = "Simulated source";
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 2;

    /// <summary>Clock error of the source relative to the laptop clock, parts per million.</summary>
    public double Ppm { get; init; }

    public int PacketFrames { get; init; } = 480;

    /// <summary>Random extra delivery delay per packet (0..JitterMs).</summary>
    public double JitterMs { get; init; } = 1.0;

    /// <summary>Windows (start, duration seconds, relative to Start) in which packets are lost.</summary>
    public IReadOnlyList<(double Start, double Duration)> Dropouts { get; init; } = [];

    public ISignal Signal { get; init; } = new MusicLikeSignal();

    public int Seed { get; init; } = 7;

    /// <summary>When set and returning false, packets are not delivered (e.g. phone paused). Positions keep advancing.</summary>
    public Func<bool>? Gate { get; init; }
}

/// <summary>
/// Simulated capture source (phone stream / loopback). Delivers packets on the virtual clock with the configured
/// clock drift, delivery jitter and dropouts. Writes only to in-memory buffers.
/// </summary>
public sealed class SimulatedSource : IAudioSource
{
    private readonly SimScheduler _scheduler;
    private readonly SimSourceOptions _options;
    private readonly float[] _packet;
    private readonly Random _rng;
    private int _generation;
    private bool _running;

    public SimulatedSource(SimScheduler scheduler, SimSourceOptions options)
    {
        _scheduler = scheduler;
        _options = options;
        _packet = new float[options.PacketFrames * options.Channels];
        _rng = new Random(options.Seed);
    }

    public string Id => _options.Id;

    public string DisplayName => _options.Name;

    public long PacketsDelivered { get; private set; }

    public long PacketsDropped { get; private set; }

    public event EventHandler<DeviceFaultEventArgs>? Faulted;

    public void Start(ISourceSink sink)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        var generation = ++_generation;
        sink.OnFormat(new AudioFormat(_options.SampleRate, _options.Channels));
        var t0 = _scheduler.Now;
        var trueRate = _options.SampleRate * (1 + (_options.Ppm * 1e-6));
        long packetIndex = 0;
        var lost = false;

        void Deliver(double now)
        {
            if (generation != _generation)
            {
                return;
            }

            var first = packetIndex * _options.PacketFrames;
            var captureStart = t0 + (first / trueRate);
            var rel = captureStart - t0;
            var dropped = false;
            foreach (var (start, duration) in _options.Dropouts)
            {
                if (rel >= start && rel < start + duration)
                {
                    dropped = true;
                    break;
                }
            }

            if (_options.Gate is { } gate && !gate())
            {
                lost = false;
            }
            else if (dropped)
            {
                PacketsDropped++;
                lost = true;
            }
            else
            {
                _options.Signal.Fill(_packet, _options.Channels, first, _options.SampleRate);
                sink.Write(_packet, captureStart, lost);
                PacketsDelivered++;
                lost = false;
            }

            packetIndex++;
            var nextEnd = t0 + ((packetIndex + 1) * _options.PacketFrames / trueRate);
            _scheduler.Schedule(nextEnd + (_rng.NextDouble() * _options.JitterMs * 0.001), Deliver);
        }

        _scheduler.Schedule(t0 + (_options.PacketFrames / trueRate), Deliver);
    }

    public void Stop()
    {
        _running = false;
        _generation++;
    }

    /// <summary>Simulates the source dying (e.g. phone walked out of range).</summary>
    public void SimulateFault()
    {
        Stop();
        Faulted?.Invoke(this, new DeviceFaultEventArgs(DeviceFaultKind.Disconnected, "simulated source fault"));
    }

    public void Dispose() => Stop();
}
