namespace Soundsys.Engine.Calibration;

/// <summary>What the mic heard from one speaker.</summary>
/// <param name="Heard">False if no convincing sweep arrival was found.</param>
/// <param name="LatencySeconds">Emit (stream time) → mic arrival.</param>
/// <param name="QualityDb">Correlation peak over the median correlation level in the search window.</param>
public sealed record SpeakerArrival(bool Heard, double LatencySeconds, double QualityDb);

/// <param name="RelativeDelayMs">How much later speaker B is heard than speaker A (negative = B earlier).</param>
public sealed record CalibrationAnalysis(SpeakerArrival A, SpeakerArrival B, double RelativeDelayMs)
{
    public bool Succeeded => A.Heard && B.Heard;
}

/// <summary>
/// Measures each speaker's arrival time in a mic recording of a <see cref="CalibrationSignal"/> using band-limited
/// generalized cross-correlation with PHAT-β weighting (sharp peaks under reverb, robust to noise), then picks the
/// earliest strong peak (the direct path) and refines it to sub-sample precision with parabolic interpolation.
/// </summary>
public static class Calibrator
{
    /// <summary>Minimum peak-to-median ratio for a sweep to count as heard.</summary>
    public const double MinQualityDb = 24;

    /// <summary>PHAT exponent: 1 = full whitening, 0 = plain cross-correlation.</summary>
    private const double PhatBeta = 0.75;

    /// <summary>How far before the strongest peak an earlier (direct-path) peak is looked for.</summary>
    private const double EarlyPeakWindowSeconds = 0.020;

    private const double EarlyPeakFraction = 0.5;

    /// <param name="mic">Mono mic recording.</param>
    /// <param name="micRate">Mic sample rate.</param>
    /// <param name="micStartTime">Clock time of <paramref name="mic"/>[0].</param>
    /// <param name="emitTimeA">Clock time at which sweep A entered the engine (stream time of plan.OffsetA).</param>
    /// <param name="emitTimeB">Clock time at which sweep B entered the engine.</param>
    public static CalibrationAnalysis Analyze(ReadOnlySpan<float> mic, int micRate, double micStartTime, double emitTimeA, double emitTimeB, CalibrationPlan plan)
    {
        var reference = Sweep.Generate(micRate, plan.SweepSeconds, plan.StartHz, plan.EndHz);
        var cc = Correlate(mic, reference, micRate, plan.StartHz, plan.EndHz);
        var a = FindArrival(cc, micRate, micStartTime, emitTimeA, plan.MaxLatencySeconds);
        var b = FindArrival(cc, micRate, micStartTime, emitTimeB, plan.MaxLatencySeconds);
        return new CalibrationAnalysis(a, b, (b.LatencySeconds - a.LatencySeconds) * 1000.0);
    }

    /// <summary>
    /// New per-speaker delays that equalise arrival times: the earlier speaker is delayed by the measured
    /// difference, then both are normalised so the smaller delay is 0 (minimum added latency).
    /// </summary>
    public static (double OffsetA, double OffsetB) ComputeOffsets(double currentA, double currentB, double relativeDelayMs)
    {
        var a = currentA + Math.Max(relativeDelayMs, 0);
        var b = currentB + Math.Max(-relativeDelayMs, 0);
        var m = Math.Min(a, b);
        return (Math.Round(a - m, 1), Math.Round(b - m, 1));
    }

    /// <summary>Band-limited GCC-PHAT-β of <paramref name="x"/> against <paramref name="reference"/>; index k = lag k samples.</summary>
    public static double[] Correlate(ReadOnlySpan<float> x, float[] reference, int rate, double f0, double f1)
    {
        var n = Fft.NextPowerOfTwo(x.Length + reference.Length);
        var xr = new double[n];
        var xi = new double[n];
        var rr = new double[n];
        var ri = new double[n];
        for (var i = 0; i < x.Length; i++)
        {
            xr[i] = x[i];
        }

        for (var i = 0; i < reference.Length; i++)
        {
            rr[i] = reference[i];
        }

        Fft.Transform(xr, xi, inverse: false);
        Fft.Transform(rr, ri, inverse: false);

        // Pass band with a little margin; everything else is noise for this measurement.
        var lo = (int)Math.Floor(f0 * 0.8 * n / rate);
        var hi = (int)Math.Ceiling(Math.Min(f1 * 1.1, rate / 2.0) * n / rate);
        for (var k = 0; k < n; k++)
        {
            var bin = k <= n / 2 ? k : n - k;
            if (bin < lo || bin > hi)
            {
                xr[k] = xi[k] = 0;
                continue;
            }

            // X · conj(R)
            var gr = (xr[k] * rr[k]) + (xi[k] * ri[k]);
            var gi = (xi[k] * rr[k]) - (xr[k] * ri[k]);
            var mag = Math.Sqrt((gr * gr) + (gi * gi));
            var w = mag > 1e-20 ? Math.Pow(mag, -PhatBeta) : 0;

            // Soft taper at the band edges reduces ringing in the correlation.
            var edge = Math.Min(Math.Min(bin - lo, hi - bin) / (0.05 * (hi - lo)), 1.0);
            xr[k] = gr * w * edge;
            xi[k] = gi * w * edge;
        }

        Fft.Transform(xr, xi, inverse: true);
        var len = x.Length;
        var cc = new double[len];
        Array.Copy(xr, cc, len);
        return cc;
    }

    private static SpeakerArrival FindArrival(double[] cc, int rate, double micStart, double emitTime, double maxLatency)
    {
        var lo = (int)Math.Floor((emitTime - micStart) * rate);
        var hi = (int)Math.Ceiling((emitTime + maxLatency - micStart) * rate);
        lo = Math.Clamp(lo, 1, cc.Length - 2);
        hi = Math.Clamp(hi, lo + 1, cc.Length - 2);

        var best = lo;
        for (var k = lo; k <= hi; k++)
        {
            if (cc[k] > cc[best])
            {
                best = k;
            }
        }

        var peak = cc[best];
        if (peak <= 0)
        {
            return new SpeakerArrival(false, double.NaN, 0);
        }

        // Direct path = the earliest local maximum within 20 ms before the strongest peak that is at least half as strong.
        var early = Math.Max(lo, best - (int)(EarlyPeakWindowSeconds * rate));
        for (var k = early; k < best; k++)
        {
            if (cc[k] >= EarlyPeakFraction * peak && cc[k] >= cc[k - 1] && cc[k] >= cc[k + 1])
            {
                best = k;
                break;
            }
        }

        // Quality: peak vs. median absolute correlation in the window.
        var window = new double[hi - lo + 1];
        for (var k = lo; k <= hi; k++)
        {
            window[k - lo] = Math.Abs(cc[k]);
        }

        Array.Sort(window);
        var median = Math.Max(window[window.Length / 2], 1e-30);
        var qualityDb = 20 * Math.Log10(cc[best] / median);

        // Parabolic sub-sample refinement.
        var y0 = cc[best - 1];
        var y1 = cc[best];
        var y2 = cc[best + 1];
        var denom = y0 - (2 * y1) + y2;
        var delta = Math.Abs(denom) > 1e-30 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0;

        var arrival = micStart + ((best + delta) / rate);
        var heard = qualityDb >= MinQualityDb;
        return new SpeakerArrival(heard, arrival - emitTime, qualityDb);
    }
}
