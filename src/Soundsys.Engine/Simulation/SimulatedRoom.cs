using Soundsys.Engine.Abstractions;

namespace Soundsys.Engine.Simulation;

/// <summary>
/// A simulated room with the laptop mic in it. Everything the simulated speakers play arrives at the mic after the speaker's
/// acoustic latency, through a sparse room response (direct path + early reflections + decaying diffuse taps), plus mic noise.
/// Lets calibration run end-to-end in simulated mode. Purely in memory: nothing is ever played.
/// </summary>
public sealed class SimulatedRoom : ISimPlaybackObserver
{
    private const int Capacity = 1 << 19; // ~10.9 s at 48 kHz
    private const int Mask = Capacity - 1;

    private readonly SimScheduler _scheduler;
    private readonly float[] _air = new float[Capacity];
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int[] Delays, float[] Gains)> _responses = [];
    private long _readIndex = -1;
    private int _listening;

    public SimulatedRoom(SimScheduler scheduler, int micRate = 48000, double noiseRms = 0.002, int seed = 3)
    {
        _scheduler = scheduler;
        MicRate = micRate;
        NoiseRms = noiseRms;
        Seed = seed;
    }

    public int MicRate { get; }

    public double NoiseRms { get; set; }

    public int Seed { get; }

    public void OnPlayed(SimulatedOutputDevice device, double dacTime, double trueRate, ReadOnlySpan<float> interleaved)
    {
        var options = device.Options;
        var (delays, gains) = ResponseFor(device.Id);
        var channels = options.Channels;
        var frames = interleaved.Length / channels;
        var arrival = dacTime + (options.AcousticLatencyMs / 1000.0);
        var gain = (float)options.GainAtMic;
        lock (_lock)
        {
            if (_listening == 0)
            {
                return; // nobody is recording; sound is not kept
            }

            for (var i = 0; i < frames; i++)
            {
                float mono = 0;
                for (var c = 0; c < channels; c++)
                {
                    mono += interleaved[(i * channels) + c];
                }

                mono *= gain / channels;
                if (mono == 0)
                {
                    continue;
                }

                var pos = (arrival + (i / trueRate)) * MicRate;
                var n = (long)Math.Floor(pos);
                var frac = (float)(pos - n);
                if (n <= _readIndex || n - _readIndex >= Capacity - 4096)
                {
                    continue; // already captured, or absurdly far ahead
                }

                for (var r = 0; r < delays.Length; r++)
                {
                    var v = mono * gains[r];
                    var idx = n + delays[r];
                    _air[idx & Mask] += v * (1 - frac);
                    _air[(idx + 1) & Mask] += v * frac;
                }
            }
        }
    }

    /// <summary>A mic capture source listening to this room.</summary>
    public IAudioSource CreateMicrophone(string id = "sim-mic", string name = "Surface Microphone Array") => new Mic(this, id, name);

    private (int[] Delays, float[] Gains) ResponseFor(string deviceId)
    {
        lock (_lock)
        {
            if (_responses.TryGetValue(deviceId, out var r))
            {
                return r;
            }

            var rng = new Random(Seed ^ deviceId.GetHashCode(StringComparison.Ordinal));
            var delays = new List<int> { 0 };
            var gains = new List<float> { 1f };
            for (var k = 0; k < 6; k++)
            {
                delays.Add((int)((0.002 + (rng.NextDouble() * 0.012)) * MicRate));
                gains.Add((float)((0.25 + (0.3 * rng.NextDouble())) * (rng.Next(2) == 0 ? -1 : 1)));
            }

            for (var k = 0; k < 40; k++)
            {
                var t = 0.01 + (rng.NextDouble() * 0.35);
                delays.Add((int)(t * MicRate));
                gains.Add((float)(0.12 * Math.Exp(-6.91 * t / 0.4) * (rng.Next(2) == 0 ? -1 : 1)));
            }

            r = (delays.ToArray(), gains.ToArray());
            _responses[deviceId] = r;
            return r;
        }
    }

    private void BeginListening(long firstIndex)
    {
        lock (_lock)
        {
            if (_listening++ == 0)
            {
                Array.Clear(_air);
                _readIndex = firstIndex - 1;
            }
        }
    }

    private void EndListening()
    {
        lock (_lock)
        {
            _listening = Math.Max(0, _listening - 1);
        }
    }

    /// <summary>Reads (and clears) mic samples for [start, start + count).</summary>
    private void Capture(long start, Span<float> dest, Random rng)
    {
        lock (_lock)
        {
            for (var i = 0; i < dest.Length; i++)
            {
                var idx = (start + i) & Mask;
                var g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                dest[i] = _air[idx] + (float)(NoiseRms * g);
                _air[idx] = 0;
            }

            _readIndex = start + dest.Length - 1;
        }
    }

    private sealed class Mic(SimulatedRoom room, string id, string name) : IAudioSource
    {
        private const int PacketFrames = 480;
        private readonly float[] _packet = new float[PacketFrames];
        private readonly Random _rng = new(room.Seed + 1);
        private int _generation;
        private bool _listening;

        public string Id => id;

        public string DisplayName => name;

#pragma warning disable CS0067
        public event EventHandler<DeviceFaultEventArgs>? Faulted;
#pragma warning restore CS0067

        public void Start(ISourceSink sink)
        {
            var generation = ++_generation;
            var rate = room.MicRate;
            sink.OnFormat(new AudioFormat(rate, 1));
            var next = (long)Math.Ceiling(room._scheduler.Now * rate);
            room.BeginListening(next);
            _listening = true;

            void Deliver(double now)
            {
                if (generation != _generation)
                {
                    return;
                }

                // Deliver every complete packet that ended before now.
                while ((next + PacketFrames) / (double)rate <= now)
                {
                    room.Capture(next, _packet, _rng);
                    sink.Write(_packet, next / (double)rate, false);
                    next += PacketFrames;
                }

                room._scheduler.ScheduleAfter(PacketFrames / (double)rate, Deliver);
            }

            room._scheduler.ScheduleAfter(PacketFrames / (double)rate, Deliver);
        }

        public void Stop()
        {
            _generation++;
            if (_listening)
            {
                _listening = false;
                room.EndListening();
            }
        }

        public void Dispose() => Stop();
    }
}
