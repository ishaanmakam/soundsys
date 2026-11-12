using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Dsp;

namespace Soundsys.Engine.Sync;

public enum ChannelState
{
    /// <summary>Not started.</summary>
    Idle,

    /// <summary>No source audio (nothing playing, or buffering after start/rejoin).</summary>
    WaitingForAudio,

    Playing,

    /// <summary>Ran out of buffered audio while the source was active (a real glitch).</summary>
    Underrun,
}

/// <summary>Settings shared by all speakers (written by the control thread, read by RT threads).</summary>
public sealed class SharedSyncSettings
{
    private double _baseLatencyMs = 50;
    private float _masterGain = 1f;

    /// <summary>Common pipeline latency target before per-speaker offsets.</summary>
    public double BaseLatencyMs
    {
        get => Volatile.Read(ref _baseLatencyMs);
        set => Volatile.Write(ref _baseLatencyMs, Math.Clamp(value, 20, 500));
    }

    public float MasterGain
    {
        get => Volatile.Read(ref _masterGain);
        set => Volatile.Write(ref _masterGain, Math.Clamp(value, 0f, 1f));
    }
}

/// <summary>Live per-speaker numbers for the UI and diagnostics.</summary>
public readonly record struct ChannelStats(
    ChannelState State,
    double LatencyMs,
    double LatencyErrorMs,
    double CorrectionPpm,
    long Underruns,
    long SourceGaps,
    long Jumps,
    float Peak);

/// <summary>
/// One speaker's pull pipeline, driven by its output device's render callback:
/// shared <see cref="HistoryBuffer"/> → (per-speaker read position = delay buffer) → resampler (SRC + drift) → gain.
/// <para>
/// Every callback it estimates the pipeline latency
/// <c>L = W_est(t) − readPos + queued·(srcRate/devRate)</c> where <c>W_est</c> interpolates the writer position to
/// the callback time from its timestamp, and steers it to <c>BaseLatency + Offset</c>: small errors through the
/// <see cref="DriftController"/> (resampling ratio), large ones (start, rejoin, offset edit) by jumping the read
/// pointer with a short crossfade. Because every speaker steers to the same writer timeline, speakers stay aligned.
/// </para>
/// Real-time safe: <see cref="Render"/> never allocates, locks or blocks.
/// </summary>
public sealed class OutputChannel : IRenderTarget
{
    /// <summary>Errors larger than this are corrected by jumping instead of slewing.</summary>
    public const double JumpThresholdSeconds = 0.015;

    /// <summary>No new source data for this long = the source is paused/idle (not an underrun).</summary>
    public const double IdleTimeoutSeconds = 0.25;

    private const double MaxExtrapolationSeconds = 0.1;
    private const double FadeSeconds = 0.005;
    private const int ChunkFrames = 256;
    private const double MaxStep = 8.0;
    private const int GuardFrames = 4096;

    private readonly HistoryBuffer _history;
    private readonly SharedSyncSettings _shared;
    private readonly PolyphaseResampler _resampler = new();
    private readonly DriftController _controller = new(48000);
    private readonly float[] _scratchLeft;
    private readonly float[] _scratchRight;
    private float[] _xfade = [];

    // Configuration (control thread → RT thread).
    private double _offsetMs;
    private float _gain = 1f;
    private long _passStart = long.MinValue;
    private long _passEnd = long.MaxValue;

    // RT state.
    private int _devRate;
    private int _channels;
    private int _fadeFrames;
    private int _epoch = -1;
    private int _srcRate;
    private bool _primed;
    private bool _fadeIn;
    private bool _starving;
    private double _readPos;
    private double _correction;
    private double _lastTimestamp = double.NaN;
    private float _currentGain;

    // Stats (RT thread writes, others read).
    private int _state;
    private double _statLatency;
    private double _statError;
    private double _statCorrection;
    private long _underruns;
    private long _sourceGaps;
    private long _jumps;
    private float _peak;
    private double _lastBlockStart = double.NaN;
    private double _lastBlockStep;

    public OutputChannel(HistoryBuffer history, SharedSyncSettings shared)
    {
        _history = history;
        _shared = shared;
        var scratch = (int)(ChunkFrames * MaxStep) + PolyphaseResampler.Taps + 8;
        _scratchLeft = new float[scratch];
        _scratchRight = new float[scratch];
    }

    /// <summary>Extra delay for this speaker (calibration / manual sync), milliseconds ≥ 0.</summary>
    public double OffsetMs
    {
        get => Volatile.Read(ref _offsetMs);
        set => Volatile.Write(ref _offsetMs, Math.Clamp(value, 0, 2000));
    }

    /// <summary>This speaker's linear gain (0..1), multiplied by the master gain.</summary>
    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Source position of the first frame rendered by the last callback (NaN if it rendered silence). For diagnostics/simulation.</summary>
    public double LastBlockStartPosition => Volatile.Read(ref _lastBlockStart);

    /// <summary>Input frames advanced per output frame in the last callback.</summary>
    public double LastBlockStep => Volatile.Read(ref _lastBlockStep);

    /// <summary>Only source positions in [start, end) are audible (used by calibration to solo each speaker).</summary>
    public void SetPassWindow(long start, long end)
    {
        Volatile.Write(ref _passStart, start);
        Volatile.Write(ref _passEnd, end);
    }

    public void ClearPassWindow() => SetPassWindow(long.MinValue, long.MaxValue);

    public ChannelStats GetStats() => new(
        (ChannelState)Volatile.Read(ref _state),
        Volatile.Read(ref _statLatency),
        Volatile.Read(ref _statError),
        Volatile.Read(ref _statCorrection),
        Volatile.Read(ref _underruns),
        Volatile.Read(ref _sourceGaps),
        Volatile.Read(ref _jumps),
        Volatile.Read(ref _peak));

    /// <inheritdoc />
    public void Prepare(AudioFormat deviceFormat, int maxFramesPerCallback)
    {
        _devRate = deviceFormat.SampleRate;
        _channels = Math.Max(1, deviceFormat.Channels);
        _fadeFrames = Math.Max(1, (int)(_devRate * FadeSeconds));
        _xfade = new float[_fadeFrames * _channels];
        _epoch = -1;
        _primed = false;
        _lastTimestamp = double.NaN;
        _currentGain = 0f;
        _correction = 0;
        Volatile.Write(ref _state, (int)ChannelState.WaitingForAudio);
    }

    /// <inheritdoc />
    public void Render(Span<float> interleaved, int frames, in RenderTiming timing)
    {
        var dest = interleaved[..(frames * _channels)];
        var t = timing.TimestampSeconds;
        var dt = double.IsNaN(_lastTimestamp) ? 0 : t - _lastTimestamp;
        _lastTimestamp = t;

        var ws = _history.ReadState();
        if (ws.SampleRate <= 0 || ws.Position == 0)
        {
            Silence(dest, ChannelState.WaitingForAudio);
            return;
        }

        if (ws.Epoch != _epoch || ws.SampleRate != _srcRate)
        {
            _epoch = ws.Epoch;
            _srcRate = ws.SampleRate;
            _controller.SetSampleRate(_srcRate);
            _correction = 0;
            _primed = false;
            _starving = false;
        }

        var sinceWrite = t - ws.EndTimestamp;
        if (sinceWrite > IdleTimeoutSeconds)
        {
            if (_starving)
            {
                // We ran dry because the source stopped (pause), not because our buffer was too small.
                _starving = false;
                _sourceGaps++;
            }

            Silence(dest, ChannelState.WaitingForAudio);
            return;
        }

        double srcRate = _srcRate;
        var nominal = srcRate / _devRate;
        var wEst = ws.Position + (Math.Clamp(sinceWrite, -MaxExtrapolationSeconds, MaxExtrapolationSeconds) * srcRate);
        var queuedSrc = timing.QueuedFrames * nominal;
        var desired = (_shared.BaseLatencyMs + OffsetMs) * 0.001 * srcRate;
        var oldestValid = ws.Position - _history.CapacityFrames + GuardFrames;

        var crossfadeFrom = double.NaN;
        if (!_primed)
        {
            var start = Math.Max(wEst + queuedSrc - desired, oldestValid + PolyphaseResampler.HalfTaps);
            if (!Available(start, nominal * (1 + DriftController.MaxCorrection), frames, ws.Position))
            {
                Silence(dest, ChannelState.WaitingForAudio);
                return;
            }

            if (_starving)
            {
                // Audio resumed quickly after we ran dry: a genuine underrun (buffer smaller than the jitter).
                _starving = false;
                _underruns++;
            }

            _readPos = start;
            _primed = true;
            _fadeIn = true;
            _controller.ResetError();
            _correction = _controller.Correction;
        }
        else
        {
            var error = wEst - _readPos + queuedSrc - desired;
            if (Math.Abs(error) > JumpThresholdSeconds * srcRate)
            {
                var target = Math.Max(_readPos + error, oldestValid + PolyphaseResampler.HalfTaps);
                if (Available(_readPos, nominal, Math.Min(frames, _fadeFrames), ws.Position))
                {
                    crossfadeFrom = _readPos;
                }
                else
                {
                    _fadeIn = true;
                }

                _readPos = target;
                _jumps++;
                _controller.ResetError();
                _correction = _controller.Correction;
            }
            else
            {
                _correction = _controller.Update(error, dt);
            }

            Volatile.Write(ref _statError, error / srcRate * 1000.0);
        }

        var step = nominal * (1 + _correction);
        if (!Available(_readPos, step, frames, ws.Position))
        {
            // Classified later: underrun if audio resumes within IdleTimeout, otherwise a source pause.
            _starving = true;
            Silence(dest, ChannelState.Underrun);
            return;
        }

        var blockStart = _readPos;
        _readPos = RenderPath(_readPos, step, dest, frames);

        if (!double.IsNaN(crossfadeFrom))
        {
            var n = Math.Min(frames, _fadeFrames);
            var xf = _xfade.AsSpan(0, n * _channels);
            RenderPath(crossfadeFrom, step, xf, n);
            for (var k = 0; k < n; k++)
            {
                var w = (k + 1) / (float)(n + 1);
                for (var c = 0; c < _channels; c++)
                {
                    var i = (k * _channels) + c;
                    dest[i] = (dest[i] * w) + (xf[i] * (1 - w));
                }
            }
        }
        else if (_fadeIn)
        {
            var n = Math.Min(frames, _fadeFrames);
            for (var k = 0; k < n; k++)
            {
                var w = (k + 1) / (float)(n + 1);
                for (var c = 0; c < _channels; c++)
                {
                    dest[(k * _channels) + c] *= w;
                }
            }
        }

        _fadeIn = false;
        ApplyPassWindow(dest, frames, blockStart, step);
        ApplyGain(dest, frames);

        var latency = wEst - blockStart + queuedSrc;
        Volatile.Write(ref _statLatency, latency / srcRate * 1000.0);
        Volatile.Write(ref _statCorrection, _correction * 1e6);
        Volatile.Write(ref _lastBlockStart, blockStart);
        Volatile.Write(ref _lastBlockStep, step);
        Volatile.Write(ref _state, (int)ChannelState.Playing);
    }

    /// <summary>Resets counters (e.g. at engine start).</summary>
    public void ResetStats()
    {
        Volatile.Write(ref _underruns, 0);
        Volatile.Write(ref _sourceGaps, 0);
        Volatile.Write(ref _jumps, 0);
    }

    private static bool Available(double pos, double step, int frames, long writerPosition)
        => pos + ((frames - 1) * step) + PolyphaseResampler.HalfTaps + 1 <= writerPosition;

    private double RenderPath(double pos, double step, Span<float> dest, int frames)
    {
        var done = 0;
        while (done < frames)
        {
            var n = Math.Min(ChunkFrames, frames - done);
            var (first, count) = PolyphaseResampler.RequiredInput(pos, step, n);
            var left = _scratchLeft.AsSpan(0, count);
            var right = _scratchRight.AsSpan(0, count);
            _history.ReadDeinterleaved(first, left, right);
            pos = _resampler.Process(left, right, first, pos, step, dest.Slice(done * _channels, n * _channels), _channels, n);
            done += n;
        }

        return pos;
    }

    private void ApplyPassWindow(Span<float> dest, int frames, double start, double step)
    {
        var lo = Volatile.Read(ref _passStart);
        var hi = Volatile.Read(ref _passEnd);
        if (lo == long.MinValue && hi == long.MaxValue)
        {
            return;
        }

        for (var k = 0; k < frames; k++)
        {
            var p = start + (k * step);
            if (p < lo || p >= hi)
            {
                dest.Slice(k * _channels, _channels).Clear();
            }
        }
    }

    private void ApplyGain(Span<float> dest, int frames)
    {
        var target = Gain * _shared.MasterGain;
        var g = _currentGain;
        var inc = (target - g) / frames;
        var peak = 0f;
        for (var k = 0; k < frames; k++)
        {
            g += inc;
            for (var c = 0; c < _channels; c++)
            {
                var i = (k * _channels) + c;
                var v = dest[i] * g;
                dest[i] = v;
                var a = Math.Abs(v);
                if (a > peak)
                {
                    peak = a;
                }
            }
        }

        _currentGain = target;
        Volatile.Write(ref _peak, peak);
    }

    private void Silence(Span<float> dest, ChannelState state)
    {
        dest.Clear();
        _currentGain = 0f;
        Volatile.Write(ref _peak, 0f);
        Volatile.Write(ref _lastBlockStart, double.NaN);
        Volatile.Write(ref _state, (int)state);
        _primed = false;
    }
}
