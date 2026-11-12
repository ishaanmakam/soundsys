namespace Soundsys.Engine.Tests;

internal static class TestSignals
{
    public static float[] Sine(int frames, double freq, double rate, double amplitude = 0.5, double phase = 0)
    {
        var x = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            x[i] = (float)(amplitude * Math.Sin((2 * Math.PI * freq * i / rate) + phase));
        }

        return x;
    }

    /// <summary>Signal-to-error ratio in dB between <paramref name="actual"/> and <paramref name="expected"/>.</summary>
    public static double SnrDb(ReadOnlySpan<float> actual, ReadOnlySpan<double> expected)
    {
        double sig = 0, err = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            sig += expected[i] * expected[i];
            var e = actual[i] - expected[i];
            err += e * e;
        }

        return 10 * Math.Log10(sig / Math.Max(err, 1e-30));
    }

    /// <summary>Deterministic music-like test signal: chords + noise bursts.</summary>
    public static float[] MusicLike(int frames, double rate, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new float[frames];
        double[] freqs = [110, 220, 277.2, 329.6, 440, 554.4, 880, 1760, 3520];
        for (var i = 0; i < frames; i++)
        {
            var t = i / rate;
            double v = 0;
            for (var k = 0; k < freqs.Length; k++)
            {
                v += Math.Sin(2 * Math.PI * freqs[k] * t * (1 + (0.001 * k))) / (k + 2);
            }

            // Percussive noise every 0.25 s.
            var beat = t % 0.25;
            if (beat < 0.03)
            {
                v += (rng.NextDouble() - 0.5) * (1 - (beat / 0.03));
            }

            x[i] = (float)(0.3 * v);
        }

        return x;
    }
}
