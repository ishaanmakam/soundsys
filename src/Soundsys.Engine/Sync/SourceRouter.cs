using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Dsp;

namespace Soundsys.Engine.Sync;

/// <summary>
/// The single writer of the shared <see cref="HistoryBuffer"/>: converts captured packets to stereo, fills capture
/// gaps with silence so the timeline keeps tracking real time (both speakers stay aligned through inbound dropouts),
/// and meters the signal (used to detect that a phone has started streaming). Runs on the capture thread; allocation-free
/// after <see cref="OnFormat"/>.
/// </summary>
public sealed class SourceRouter : ISourceSink
{
    /// <summary>Gaps shorter than this are timestamp jitter, not lost audio.</summary>
    public const double MinGapSeconds = 0.003;

    /// <summary>Gaps longer than this are a pause/restart; the timeline is not padded (speakers re-prime instead).</summary>
    public const double MaxGapSeconds = 1.0;

    private const int ChunkFrames = 2048;

    private readonly HistoryBuffer _history;
    private readonly float[] _stereo = new float[ChunkFrames * 2];
    private AudioFormat _format;
    private double _expectedNext = double.NaN;
    private double _level;
    private double _lastWrite = double.NaN;
    private long _gapFrames;
    private long _packets;

    public SourceRouter(HistoryBuffer history) => _history = history;

    public HistoryBuffer History => _history;

    /// <summary>Current input format (default until the first <see cref="OnFormat"/>).</summary>
    public AudioFormat Format => _format;

    /// <summary>Smoothed RMS level of the input (linear, 0..1).</summary>
    public double Level => Volatile.Read(ref _level);

    /// <summary>Capture timestamp of the most recent packet (NaN if none).</summary>
    public double LastWriteTimestamp => Volatile.Read(ref _lastWrite);

    /// <summary>Total silence frames inserted to cover capture gaps.</summary>
    public long GapFrames => Volatile.Read(ref _gapFrames);

    public long Packets => Volatile.Read(ref _packets);

    public void OnFormat(AudioFormat format)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            throw new ArgumentException("Invalid source format.", nameof(format));
        }

        var state = _history.ReadState();
        if (state.SampleRate != format.SampleRate)
        {
            _history.Reset(format.SampleRate);
            _expectedNext = double.NaN;
        }

        // Same rate (e.g. switching phone → calibration sweep → phone): keep the timeline continuous. The gap between
        // the old source's last frame and the new source's first is filled with silence like any capture gap, so every
        // speaker keeps a consistent position↔time mapping and nobody has to slew or jump.
        _format = format;
    }

    public void Write(ReadOnlySpan<float> interleaved, double timestampSeconds, bool discontinuity)
    {
        var channels = _format.Channels;
        var rate = _format.SampleRate;
        if (channels <= 0)
        {
            return;
        }

        var frames = interleaved.Length / channels;
        if (!double.IsNaN(_expectedNext))
        {
            var gap = timestampSeconds - _expectedNext;
            if (gap > MinGapSeconds && gap < MaxGapSeconds)
            {
                var silent = (int)Math.Round(gap * rate);
                _history.WriteSilence(silent, _expectedNext);
                Volatile.Write(ref _gapFrames, _gapFrames + silent);
            }
        }

        double energy = 0;
        var done = 0;
        while (done < frames)
        {
            var n = Math.Min(ChunkFrames, frames - done);
            var dst = _stereo.AsSpan(0, n * 2);
            var src = interleaved.Slice(done * channels, n * channels);
            for (var i = 0; i < n; i++)
            {
                float l, r;
                if (channels == 1)
                {
                    l = r = src[i];
                }
                else
                {
                    l = src[i * channels];
                    r = src[(i * channels) + 1];
                }

                dst[2 * i] = l;
                dst[(2 * i) + 1] = r;
                energy += (l * l) + (r * r);
            }

            _history.Write(dst, timestampSeconds + (done / (double)rate));
            done += n;
        }

        _expectedNext = timestampSeconds + (frames / (double)rate);
        if (frames > 0)
        {
            var rms = Math.Sqrt(energy / (2.0 * frames));
            Volatile.Write(ref _level, (_level * 0.8) + (rms * 0.2));
        }

        Volatile.Write(ref _lastWrite, timestampSeconds);
        Volatile.Write(ref _packets, _packets + 1);
    }
}
