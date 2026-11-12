using Soundsys.Engine.Dsp;

namespace Soundsys.Engine.Tests.Dsp;

public class ResamplerTests
{
    private static (float[] Out, double EndPos) Run(float[] input, double pos, double step, int frames, double cutoff = PolyphaseResampler.DefaultCutoff)
    {
        var rs = new PolyphaseResampler(cutoff);
        var output = new float[frames * 2];
        var end = rs.Process(input, input, baseFrame: 0, pos, step, output, outChannels: 2, frames);
        var left = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = output[2 * i];
            Assert.Equal(output[2 * i], output[(2 * i) + 1]);
        }

        return (left, end);
    }

    private static double[] Expected(double freq, double rate, double pos, double step, int frames)
    {
        var e = new double[frames];
        for (var k = 0; k < frames; k++)
        {
            e[k] = 0.5 * Math.Sin(2 * Math.PI * freq * (pos + (k * step)) / rate);
        }

        return e;
    }

    [Fact]
    public void Required_input_range_covers_all_taps()
    {
        var (first, count) = PolyphaseResampler.RequiredInput(100.25, 1.0, 10);
        Assert.Equal(100 - PolyphaseResampler.HalfTaps + 1, first);
        Assert.Equal((109 + PolyphaseResampler.HalfTaps) - first + 1, count);
    }

    [Fact]
    public void Returns_position_after_last_output_frame()
    {
        var input = TestSignals.Sine(4096, 1000, 48000);
        var (_, end) = Run(input, 100.5, 1.25, 1000);
        Assert.Equal(100.5 + (1000 * 1.25), end, 9);
    }

    [Theory]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public void Unity_ratio_reproduces_band_limited_input(double freq)
    {
        var input = TestSignals.Sine(8192, freq, 48000);
        var (output, _) = Run(input, 1000, 1.0, 4096);
        var snr = TestSignals.SnrDb(output, Expected(freq, 48000, 1000, 1.0, 4096));
        Assert.True(snr > 80, $"SNR {snr:F1} dB");
    }

    [Theory]
    [InlineData(1000.0, 85)]
    [InlineData(10000.0, 75)]
    public void Converts_44k1_to_48k_with_high_snr(double freq, double minSnr)
    {
        const int frames = 4096;
        var input = TestSignals.Sine(8192, freq, 44100);
        var step = 44100.0 / 48000.0;
        var (output, _) = Run(input, 500.3, step, frames);
        var snr = TestSignals.SnrDb(output, Expected(freq, 44100, 500.3, step, frames));
        Assert.True(snr > minSnr, $"SNR {snr:F1} dB");
    }

    [Theory]
    [InlineData(1.001)]
    [InlineData(0.999)]
    [InlineData(1.00005)]
    public void Tracks_drift_correction_ratios_accurately(double step)
    {
        const int frames = 4096;
        var input = TestSignals.Sine(8192, 3000, 48000);
        var (output, _) = Run(input, 777.77, step, frames);
        var snr = TestSignals.SnrDb(output, Expected(3000, 48000, 777.77, step, frames));
        Assert.True(snr > 80, $"SNR {snr:F1} dB");
    }

    [Fact]
    public void Down_conversion_attenuates_content_above_new_nyquist()
    {
        // 48k -> 32k: a 20 kHz tone would alias to 12 kHz without the anti-alias cutoff.
        const int frames = 4096;
        var step = 48000.0 / 32000.0;
        var input = TestSignals.Sine(16384, 20000, 48000);
        var (output, _) = Run(input, 200, step, frames, PolyphaseResampler.CutoffFor(step));
        var rms = Math.Sqrt(output.Select(v => (double)v * v).Average());
        Assert.True(20 * Math.Log10(rms / (0.5 / Math.Sqrt(2))) < -40, $"alias level {20 * Math.Log10(rms / 0.3536):F1} dB");
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var rs = new PolyphaseResampler();
        var input = TestSignals.Sine(8192, 1000, 48000);
        var output = new float[480 * 2];
        rs.Process(input, input, 0, 100, 1.0001, output, 2, 480); // warm-up (JIT)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var pos = 100.0;
        for (var i = 0; i < 1000; i++)
        {
            pos = rs.Process(input, input, 0, 100 + (i % 7), 1.0001, output, 2, 480);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(pos > 0);
    }

    [Fact]
    public void Maps_stereo_to_other_channel_counts()
    {
        var rs = new PolyphaseResampler();
        var left = Enumerable.Repeat(0.5f, 256).ToArray();
        var right = Enumerable.Repeat(-0.25f, 256).ToArray();
        var mono = new float[10];
        rs.Process(left, right, 0, 100, 1.0, mono, 1, 10);
        Assert.All(mono, v => Assert.Equal(0.125f, v, 3));

        var quad = new float[40];
        rs.Process(left, right, 0, 100, 1.0, quad, 4, 10);
        Assert.Equal(0.5f, quad[0], 3);
        Assert.Equal(-0.25f, quad[1], 3);
        Assert.Equal(0f, quad[2]);
        Assert.Equal(0f, quad[3]);
    }
}
