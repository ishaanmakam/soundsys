using Soundsys.Engine.Dsp;

namespace Soundsys.Engine.Calibration;

/// <summary>Exponential (log) sine sweep with Tukey fades, the calibration test signal.</summary>
public static class Sweep
{
    public const double DefaultStartHz = 300;
    public const double DefaultEndHz = 8000;

    /// <summary>Value of the sweep at time <paramref name="t"/> seconds (0 outside [0, duration)).</summary>
    public static double At(double t, double duration, double f0 = DefaultStartHz, double f1 = DefaultEndHz, double fade = 0.02)
    {
        if (t < 0 || t >= duration)
        {
            return 0;
        }

        var k = Math.Log(f1 / f0);
        var phase = 2 * Math.PI * f0 * duration / k * (Math.Exp(t / duration * k) - 1);
        var env = 1.0;
        if (t < fade)
        {
            env = 0.5 * (1 - Math.Cos(Math.PI * t / fade));
        }
        else if (t > duration - fade)
        {
            env = 0.5 * (1 - Math.Cos(Math.PI * (duration - t) / fade));
        }

        return env * Math.Sin(phase);
    }

    public static float[] Generate(int sampleRate, double duration, double f0 = DefaultStartHz, double f1 = DefaultEndHz)
    {
        var n = (int)(duration * sampleRate);
        var x = new float[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = (float)At(i / (double)sampleRate, duration, f0, f1);
        }

        return x;
    }
}

/// <summary>Timing of a calibration run (seconds, relative to the start of the calibration stream).</summary>
public sealed record CalibrationPlan
{
    public double LeadInSeconds { get; init; } = 0.5;
    public double SweepSeconds { get; init; } = 1.0;

    /// <summary>Time between the start of sweep A and sweep B; tolerates latency differences up to ±(Gap − MaxLatency).</summary>
    public double GapSeconds { get; init; } = 1.5;

    /// <summary>Longest plausible emit→mic latency (engine + Bluetooth + speaker + mic).</summary>
    public double MaxLatencySeconds { get; init; } = 1.2;

    public double TailSeconds { get; init; } = 1.6;
    public double StartHz { get; init; } = Sweep.DefaultStartHz;
    public double EndHz { get; init; } = Sweep.DefaultEndHz;
    public double Amplitude { get; init; } = 0.5;

    public double OffsetA => LeadInSeconds;

    public double OffsetB => LeadInSeconds + GapSeconds;

    public double TotalSeconds => OffsetB + SweepSeconds + TailSeconds;
}

/// <summary>The calibration stream: silence, sweep (for speaker A), sweep (for speaker B), silence.</summary>
public sealed class CalibrationSignal(CalibrationPlan plan) : ISignal
{
    public void Fill(Span<float> interleaved, int channels, long firstFrame, int sampleRate)
    {
        var frames = interleaved.Length / channels;
        for (var i = 0; i < frames; i++)
        {
            var t = (firstFrame + i) / (double)sampleRate;
            var v = Sweep.At(t - plan.OffsetA, plan.SweepSeconds, plan.StartHz, plan.EndHz)
                  + Sweep.At(t - plan.OffsetB, plan.SweepSeconds, plan.StartHz, plan.EndHz);
            var s = (float)(plan.Amplitude * v);
            for (var c = 0; c < channels; c++)
            {
                interleaved[(i * channels) + c] = s;
            }
        }
    }
}

/// <summary>Manual-sync click track: a short 2.5 kHz tick every <c>interval</c> seconds on both speakers.</summary>
public sealed class ClickTrackSignal(double intervalSeconds = 0.5, double amplitude = 0.4) : ISignal
{
    public void Fill(Span<float> interleaved, int channels, long firstFrame, int sampleRate)
    {
        var frames = interleaved.Length / channels;
        for (var i = 0; i < frames; i++)
        {
            var t = (firstFrame + i) / (double)sampleRate;
            var local = t % intervalSeconds;
            var v = local < 0.012 ? amplitude * Math.Sin(2 * Math.PI * 2500 * local) * Math.Exp(-local / 0.003) : 0;
            for (var c = 0; c < channels; c++)
            {
                interleaved[(i * channels) + c] = (float)v;
            }
        }
    }
}

/// <summary>A steady sine tone (diagnostics: distinct tones per speaker).</summary>
public sealed class ToneSignal(double frequency, double amplitude = 0.25) : ISignal
{
    public void Fill(Span<float> interleaved, int channels, long firstFrame, int sampleRate)
    {
        var frames = interleaved.Length / channels;
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (firstFrame + i) / sampleRate));
            for (var c = 0; c < channels; c++)
            {
                interleaved[(i * channels) + c] = v;
            }
        }
    }
}
