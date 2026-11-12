using System.Diagnostics;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Dsp;

namespace Soundsys.Engine;

/// <summary>Monotonic clock on the same time base as WASAPI QPC timestamps.</summary>
public sealed class StopwatchClock : IClock
{
    public static StopwatchClock Instance { get; } = new();

    public double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>Real-time periodic timers, one background thread per timer (used outside simulation).</summary>
public sealed class ThreadTimerScheduler(IClock clock) : ITimerScheduler
{
    public IClock Clock => clock;

    public IDisposable SchedulePeriodic(double periodSeconds, Action<double> tick)
    {
        var handle = new Handle();
        var thread = new Thread(() =>
        {
            var next = clock.NowSeconds + periodSeconds;
            while (!handle.Disposed)
            {
                var wait = next - clock.NowSeconds;
                if (wait > 0.0015)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait - 0.001, 0.25)));
                    continue;
                }

                if (wait > 0)
                {
                    Thread.Yield();
                    continue;
                }

                try
                {
                    tick(clock.NowSeconds);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"soundsys timer: {ex}");
                }

                next += periodSeconds;
                if (clock.NowSeconds - next > 1)
                {
                    next = clock.NowSeconds + periodSeconds; // fell far behind (sleep/hibernate)
                }
            }
        })
        {
            IsBackground = true,
            Name = "soundsys-timer",
            Priority = periodSeconds < 0.05 ? ThreadPriority.Highest : ThreadPriority.Normal,
        };
        thread.Start();
        return handle;
    }

    private sealed class Handle : IDisposable
    {
        public volatile bool Disposed;

        public void Dispose() => Disposed = true;
    }
}

/// <summary>
/// Generates a signal in real time (calibration sweeps, click track, diagnostics tones). Timestamps follow the scheduler
/// clock exactly, so the emission time of any frame is known: <see cref="TimeOfFrame"/>.
/// </summary>
public sealed class GeneratorSource(ITimerScheduler scheduler, ISignal signal, string name, int sampleRate = 48000) : IAudioSource
{
    private const double Period = 0.005;
    private readonly float[] _buffer = new float[2048 * 2];
    private IDisposable? _timer;
    private ISourceSink? _sink;
    private double _t0;
    private long _written;

    public string Id => "generator:" + name;

    public string DisplayName => name;

    public int SampleRate => sampleRate;

    /// <summary>Clock time at which the generator started (frame 0).</summary>
    public double StartTime => _t0;

    public long FramesWritten => Interlocked.Read(ref _written);

#pragma warning disable CS0067 // generators never fault
    public event EventHandler<DeviceFaultEventArgs>? Faulted;
#pragma warning restore CS0067

    public double TimeOfFrame(long frame) => _t0 + (frame / (double)sampleRate);

    public void Start(ISourceSink sink)
    {
        Stop();
        _sink = sink;
        _written = 0;
        sink.OnFormat(new AudioFormat(sampleRate, 2));
        _t0 = scheduler.Clock.NowSeconds;
        _timer = scheduler.SchedulePeriodic(Period, Tick);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    private void Tick(double now)
    {
        var sink = _sink;
        if (sink is null || _timer is null)
        {
            return;
        }

        var due = (long)Math.Floor((now - _t0) * sampleRate) - _written;
        while (due > 0)
        {
            var n = (int)Math.Min(due, _buffer.Length / 2);
            var span = _buffer.AsSpan(0, n * 2);
            signal.Fill(span, 2, _written, sampleRate);
            sink.Write(span, TimeOfFrame(_written), false);
            Interlocked.Add(ref _written, n);
            due -= n;
        }
    }
}

/// <summary>Records a mono mic signal on its own timeline (packet timestamps place samples, so gaps stay gaps).</summary>
public sealed class MicRecorder(double maxSeconds) : ISourceSink
{
    private float[] _samples = [];
    private int _channels = 1;
    private int _length;

    public int SampleRate { get; private set; }

    /// <summary>Clock time of sample 0 (NaN until the first packet).</summary>
    public double StartTime { get; private set; } = double.NaN;

    public ReadOnlySpan<float> Samples => _samples.AsSpan(0, Volatile.Read(ref _length));

    public void OnFormat(AudioFormat format)
    {
        SampleRate = format.SampleRate;
        _channels = Math.Max(1, format.Channels);
        _samples = new float[(int)(maxSeconds * format.SampleRate)];
        _length = 0;
        StartTime = double.NaN;
    }

    public void Write(ReadOnlySpan<float> interleaved, double timestampSeconds, bool discontinuity)
    {
        if (double.IsNaN(StartTime))
        {
            StartTime = timestampSeconds;
        }

        var frames = interleaved.Length / _channels;
        var at = (int)Math.Round((timestampSeconds - StartTime) * SampleRate);
        for (var i = 0; i < frames; i++)
        {
            var idx = at + i;
            if (idx < 0 || idx >= _samples.Length)
            {
                continue;
            }

            float sum = 0;
            for (var c = 0; c < _channels; c++)
            {
                sum += interleaved[(i * _channels) + c];
            }

            _samples[idx] = sum / _channels;
        }

        var end = Math.Min(at + frames, _samples.Length);
        if (end > _length)
        {
            Volatile.Write(ref _length, end);
        }
    }
}
