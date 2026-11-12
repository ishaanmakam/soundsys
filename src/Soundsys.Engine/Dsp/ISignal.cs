namespace Soundsys.Engine.Dsp;

/// <summary>A deterministic signal: fills interleaved frames for absolute frame indices (test tones, sweeps, clicks, simulation).</summary>
public interface ISignal
{
    void Fill(Span<float> interleaved, int channels, long firstFrame, int sampleRate);
}
