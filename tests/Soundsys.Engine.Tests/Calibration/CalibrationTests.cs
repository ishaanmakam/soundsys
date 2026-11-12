using Soundsys.Engine.Calibration;
using Xunit.Abstractions;

namespace Soundsys.Engine.Tests.Calibration;

public class CalibrationTests(ITestOutputHelper output)
{
    private const int Rate = 48000;
    private static readonly CalibrationPlan Plan = new();

    /// <summary>A synthetic room recording of the calibration stream with exactly known speaker latencies.</summary>
    internal sealed record Scenario(double LatA, double LatB, double SnrDb, double Rt60, double GainB = 0.6, double OccludedReflection = 0)
    {
        public override string ToString() => $"A {LatA} ms, B {LatB} ms, SNR {SnrDb} dB, RT60 {Rt60} s" + (OccludedReflection > 0 ? $", reflection {OccludedReflection}x direct" : "");
    }

    private const double MicStart = 100.0;
    private const double EmitStart = MicStart + 0.25; // the calibration stream started 250 ms into the recording

    internal static float[] Record(Scenario s, int seed)
    {
        var rng = new Random(seed);
        var length = (int)((0.25 + Plan.TotalSeconds + 0.6) * Rate);
        var direct = new double[2][];
        double[] gains = [0.7, s.GainB];
        double[] lats = [s.LatA / 1000, s.LatB / 1000];
        double[] offsets = [Plan.OffsetA, Plan.OffsetB];
        for (var sp = 0; sp < 2; sp++)
        {
            direct[sp] = new double[length];
            var arrival = EmitStart + offsets[sp] + lats[sp];
            for (var n = 0; n < length; n++)
            {
                var t = MicStart + (n / (double)Rate);
                direct[sp][n] = gains[sp] * Plan.Amplitude * Sweep.At(t - arrival, Plan.SweepSeconds);
            }
        }

        var mix = new double[length];
        for (var sp = 0; sp < 2; sp++)
        {
            var ir = RoomImpulse(s.Rt60, s.OccludedReflection, rng);
            var wet = ir.Length > 1 ? Convolve(direct[sp], ir) : direct[sp];
            for (var n = 0; n < length; n++)
            {
                mix[n] += wet[n];
            }
        }

        // Noise referenced to the direct sweep power of speaker A, plus mains hum.
        var sweepRms = 0.7 * Plan.Amplitude / Math.Sqrt(2);
        var noiseStd = sweepRms / Math.Pow(10, s.SnrDb / 20);
        var recording = new float[length];
        for (var n = 0; n < length; n++)
        {
            var g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            var hum = 0.05 * Math.Sin(2 * Math.PI * 60 * n / Rate);
            recording[n] = (float)(mix[n] + (noiseStd * g) + hum);
        }

        return recording;
    }

    /// <summary>Direct path + early reflections + exponentially decaying diffuse tail (DRR ≈ 0 dB at RT60 0.6 s).</summary>
    private static double[] RoomImpulse(double rt60, double occludedReflection, Random rng)
    {
        if (rt60 <= 0 && occludedReflection <= 0)
        {
            return [1.0];
        }

        var len = (int)(Math.Max(rt60, 0.05) * Rate);
        var h = new double[len];
        h[0] = 1.0;
        for (var r = 0; r < 6; r++)
        {
            var d = (int)((0.0015 + (rng.NextDouble() * 0.012)) * Rate);
            h[d] += (0.3 + (0.3 * rng.NextDouble())) * (rng.Next(2) == 0 ? -1 : 1);
        }

        if (occludedReflection > 0)
        {
            h[(int)(0.004 * Rate)] += occludedReflection; // e.g. speaker behind the laptop: wall bounce louder than direct
        }

        for (var n = (int)(0.005 * Rate); n < len && rt60 > 0; n++)
        {
            var g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            h[n] += 0.022 * g * Math.Exp(-6.91 * n / (rt60 * Rate));
        }

        return h;
    }

    private static double[] Convolve(double[] x, double[] h)
    {
        var n = Fft.NextPowerOfTwo(x.Length + h.Length);
        var ar = new double[n];
        var ai = new double[n];
        var br = new double[n];
        var bi = new double[n];
        Array.Copy(x, ar, x.Length);
        Array.Copy(h, br, h.Length);
        Fft.Transform(ar, ai, false);
        Fft.Transform(br, bi, false);
        for (var k = 0; k < n; k++)
        {
            var re = (ar[k] * br[k]) - (ai[k] * bi[k]);
            var im = (ar[k] * bi[k]) + (ai[k] * br[k]);
            ar[k] = re;
            ai[k] = im;
        }

        Fft.Transform(ar, ai, true);
        var y = new double[x.Length];
        Array.Copy(ar, y, x.Length);
        return y;
    }

    public static TheoryData<double, double, double, double> Grid()
    {
        var data = new TheoryData<double, double, double, double>();
        (double, double)[] pairs = [(0, 57), (180, 240), (240, 183.3), (420, 3.3)];
        foreach (var (a, b) in pairs)
        {
            foreach (var snr in new[] { 30.0, 10.0, 0.0 })
            {
                foreach (var rt in new[] { 0.0, 0.3, 0.6 })
                {
                    data.Add(a, b, snr, rt);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Measures_relative_delay_within_2ms(double latA, double latB, double snr, double rt60)
    {
        var s = new Scenario(latA, latB, snr, rt60);
        var mic = Record(s, seed: (int)(latA + (latB * 7) + (snr * 13) + (rt60 * 1000)));
        var result = Calibrator.Analyze(mic, Rate, MicStart, EmitStart + Plan.OffsetA, EmitStart + Plan.OffsetB, Plan);

        var error = result.RelativeDelayMs - (latB - latA);
        output.WriteLine($"{s}: measured {result.RelativeDelayMs:F3} ms (error {error:+0.000;-0.000} ms), quality A {result.A.QualityDb:F0} dB, B {result.B.QualityDb:F0} dB");
        Assert.True(result.Succeeded, "both speakers should be heard");
        Assert.True(Math.Abs(error) < 2.0, $"error {error:F3} ms");
        Assert.True(Math.Abs((result.A.LatencySeconds * 1000) - latA) < 2.0, $"A absolute {result.A.LatencySeconds * 1000:F3} ms");
    }

    [Fact]
    public void Picks_the_direct_path_when_a_reflection_is_louder()
    {
        var s = new Scenario(200, 260, 10, 0.4, OccludedReflection: 1.6);
        var mic = Record(s, 5);
        var result = Calibrator.Analyze(mic, Rate, MicStart, EmitStart + Plan.OffsetA, EmitStart + Plan.OffsetB, Plan);
        output.WriteLine($"{s}: measured {result.RelativeDelayMs:F3} ms");
        Assert.True(Math.Abs(result.RelativeDelayMs - 60) < 2.0, $"measured {result.RelativeDelayMs:F3} ms");
    }

    [Fact]
    public void Reports_a_silent_speaker_as_not_heard()
    {
        var s = new Scenario(180, 240, 10, 0.3, GainB: 0);
        var mic = Record(s, 9);
        var result = Calibrator.Analyze(mic, Rate, MicStart, EmitStart + Plan.OffsetA, EmitStart + Plan.OffsetB, Plan);
        output.WriteLine($"quality A {result.A.QualityDb:F1} dB, B {result.B.QualityDb:F1} dB");
        Assert.True(result.A.Heard);
        Assert.False(result.B.Heard);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(0, 0, 60, 60, 0)]
    [InlineData(0, 0, -25, 0, 25)]
    [InlineData(60, 0, 2, 62, 0)]
    [InlineData(60, 0, -5, 55, 0)]
    [InlineData(10, 30, 0, 0, 20)]
    public void Offsets_delay_the_earlier_speaker_and_normalise(double curA, double curB, double rel, double expA, double expB)
    {
        var (a, b) = Calibrator.ComputeOffsets(curA, curB, rel);
        Assert.Equal(expA, a, 6);
        Assert.Equal(expB, b, 6);
    }

    [Fact]
    public void Fft_round_trips_and_finds_a_tone()
    {
        var re = new double[1024];
        var im = new double[1024];
        for (var i = 0; i < re.Length; i++)
        {
            re[i] = Math.Cos(2 * Math.PI * 10 * i / 1024.0);
        }

        var orig = (double[])re.Clone();
        Fft.Transform(re, im, false);
        Assert.Equal(512, re[10], 6);
        Assert.Equal(512, re[1014], 6);
        Assert.True(Math.Abs(re[11]) < 1e-9);
        Fft.Transform(re, im, true);
        for (var i = 0; i < re.Length; i++)
        {
            Assert.Equal(orig[i], re[i], 9);
        }
    }

    [Fact]
    public void Click_track_and_tone_signals_are_bounded()
    {
        var buf = new float[48000 * 2];
        new ClickTrackSignal().Fill(buf, 2, 0, Rate);
        Assert.True(buf.Max(Math.Abs) <= 0.4f);
        Assert.True(buf.Count(v => v != 0) > 0);
        new ToneSignal(1000).Fill(buf, 2, 0, Rate);
        Assert.InRange(buf.Max(), 0.24f, 0.2501f);
    }
}
