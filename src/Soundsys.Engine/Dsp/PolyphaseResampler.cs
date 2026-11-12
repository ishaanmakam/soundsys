using System.Numerics;

namespace Soundsys.Engine.Dsp;

/// <summary>
/// Variable-ratio polyphase windowed-sinc interpolator (32 taps, Kaiser β=9, 256 phases with linear
/// interpolation between phases). One stage performs both sample-rate conversion and clock-drift correction.
/// The filter is centred on the output position (non-causal by <see cref="HalfTaps"/> input frames ≈ 0.33 ms
/// at 48 kHz). <see cref="Process"/> never allocates.
/// </summary>
public sealed class PolyphaseResampler
{
    public const int Taps = 32;
    public const int HalfTaps = Taps / 2;
    public const int Phases = 256;
    public const double DefaultCutoff = 0.95;
    private const double KaiserBeta = 9.0;

    private readonly float[] _table; // (Phases + 1) rows of Taps coefficients
    private readonly float[] _coef = new float[Taps];

    public PolyphaseResampler(double cutoff = DefaultCutoff)
    {
        if (cutoff is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoff));
        }

        Cutoff = cutoff;
        _table = BuildTable(cutoff);
    }

    /// <summary>Normalised cutoff (fraction of the input Nyquist frequency).</summary>
    public double Cutoff { get; }

    /// <summary>Anti-alias cutoff for a given step (input frames per output frame).</summary>
    public static double CutoffFor(double step) => DefaultCutoff * Math.Min(1.0, 1.0 / step);

    /// <summary>The absolute input frame range needed to produce <paramref name="frames"/> outputs starting at <paramref name="pos"/>.</summary>
    public static (long First, int Count) RequiredInput(double pos, double step, int frames)
    {
        var first = (long)Math.Floor(pos) - (HalfTaps - 1);
        var last = (long)Math.Floor(pos + ((frames - 1) * step)) + HalfTaps;
        return (first, (int)(last - first + 1));
    }

    /// <summary>
    /// Interpolates <paramref name="frames"/> output frames at input positions <c>pos + k·step</c>.
    /// <paramref name="inLeft"/>[j] / <paramref name="inRight"/>[j] hold absolute input frame <c>baseFrame + j</c>
    /// and must cover <see cref="RequiredInput"/>. Output is interleaved with <paramref name="outChannels"/>
    /// channels (1 = mono downmix, 2 = stereo, more = stereo + silent extra channels).
    /// </summary>
    /// <returns>The input position following the last output frame.</returns>
    public double Process(
        ReadOnlySpan<float> inLeft,
        ReadOnlySpan<float> inRight,
        long baseFrame,
        double pos,
        double step,
        Span<float> output,
        int outChannels,
        int frames)
    {
        var table = _table.AsSpan();
        var coef = _coef.AsSpan();
        for (var k = 0; k < frames; k++)
        {
            var p = pos + (k * step);
            var i = (long)Math.Floor(p);
            var phase = (p - i) * Phases;
            var row = (int)phase;
            if (row >= Phases)
            {
                row = Phases - 1;
            }

            var a = (float)(phase - row);
            var start = (int)(i - (HalfTaps - 1) - baseFrame);
            Interpolate(table.Slice(row * Taps, Taps), table.Slice((row + 1) * Taps, Taps), a, coef);
            var l = Dot(inLeft.Slice(start, Taps), coef);
            var r = Dot(inRight.Slice(start, Taps), coef);

            var o = k * outChannels;
            switch (outChannels)
            {
                case 1:
                    output[o] = 0.5f * (l + r);
                    break;
                case 2:
                    output[o] = l;
                    output[o + 1] = r;
                    break;
                default:
                    output[o] = l;
                    output[o + 1] = r;
                    output.Slice(o + 2, outChannels - 2).Clear();
                    break;
            }
        }

        return pos + (frames * step);
    }

    private static void Interpolate(ReadOnlySpan<float> rowA, ReadOnlySpan<float> rowB, float a, Span<float> dest)
    {
        var w = Vector<float>.Count;
        var va = new Vector<float>(a);
        var j = 0;
        for (; j <= Taps - w; j += w)
        {
            var x = new Vector<float>(rowA[j..]);
            var y = new Vector<float>(rowB[j..]);
            (x + ((y - x) * va)).CopyTo(dest[j..]);
        }

        for (; j < Taps; j++)
        {
            dest[j] = rowA[j] + ((rowB[j] - rowA[j]) * a);
        }
    }

    private static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> h)
    {
        var w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        var j = 0;
        for (; j <= Taps - w; j += w)
        {
            acc += new Vector<float>(x[j..]) * new Vector<float>(h[j..]);
        }

        var sum = Vector.Sum(acc);
        for (; j < Taps; j++)
        {
            sum += x[j] * h[j];
        }

        return sum;
    }

    private static float[] BuildTable(double cutoff)
    {
        var table = new float[(Phases + 1) * Taps];
        var i0Beta = BesselI0(KaiserBeta);
        for (var r = 0; r <= Phases; r++)
        {
            var frac = r / (double)Phases;
            double sum = 0;
            var row = new double[Taps];
            for (var j = 0; j < Taps; j++)
            {
                var d = j - (HalfTaps - 1) - frac;
                var x = d / HalfTaps;
                var window = Math.Abs(x) >= 1 ? 0 : BesselI0(KaiserBeta * Math.Sqrt(1 - (x * x))) / i0Beta;
                row[j] = cutoff * Sinc(cutoff * d) * window;
                sum += row[j];
            }

            for (var j = 0; j < Taps; j++)
            {
                table[(r * Taps) + j] = (float)(row[j] / sum); // unity DC gain for every phase
            }
        }

        return table;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        var q = x * x / 4;
        for (var k = 1; k < 50; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
