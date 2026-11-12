using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Dsp;
using Soundsys.Engine.Sync;

namespace Soundsys.Engine.Tests.Sync;

public class OutputChannelTests
{
    private const int Rate = 48000;

    private static (HistoryBuffer History, SharedSyncSettings Shared, OutputChannel Channel) Create(double baseLatencyMs = 50)
    {
        var history = new HistoryBuffer(1 << 16);
        var shared = new SharedSyncSettings { BaseLatencyMs = baseLatencyMs };
        var channel = new OutputChannel(history, shared);
        channel.Prepare(new AudioFormat(Rate, 2), 1440);
        return (history, shared, channel);
    }

    /// <summary>Writes <paramref name="seconds"/> of a 1 kHz tone ending at <paramref name="endTime"/>.</summary>
    private static void Feed(HistoryBuffer history, double endTime, double seconds)
    {
        if (history.ReadState().SampleRate == 0)
        {
            history.Reset(Rate);
        }

        var frames = (int)(seconds * Rate);
        var data = new float[frames * 2];
        var startPos = history.ReadState().Position;
        for (var i = 0; i < frames; i++)
        {
            data[2 * i] = data[(2 * i) + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * (startPos + i) / Rate));
        }

        history.Write(data, endTime - seconds);
    }

    [Fact]
    public void Renders_silence_and_waits_when_there_is_no_source()
    {
        var (_, _, ch) = Create();
        var buf = new float[960];
        buf.AsSpan().Fill(1f);
        ch.Render(buf, 480, new RenderTiming(1.0, 480));
        Assert.All(buf, v => Assert.Equal(0f, v));
        Assert.Equal(ChannelState.WaitingForAudio, ch.GetStats().State);
    }

    [Fact]
    public void Primes_at_base_latency_plus_offset_behind_the_writer()
    {
        var (history, _, ch) = Create(baseLatencyMs: 50);
        ch.OffsetMs = 20;
        Feed(history, endTime: 1.0, seconds: 0.5);
        var buf = new float[960];
        ch.Render(buf, 480, new RenderTiming(1.0, QueuedFrames: 480));

        // W = 24000 at t=1.0; latency target 70 ms = 3360 frames; 480 frames already queued.
        Assert.Equal(24000 + 480 - 3360, ch.LastBlockStartPosition, 6);
        Assert.Equal(ChannelState.Playing, ch.GetStats().State);
    }

    [Fact]
    public void Interpolates_writer_position_between_packets()
    {
        var (history, _, ch) = Create();
        Feed(history, endTime: 1.0, seconds: 0.5);
        var buf = new float[960];
        ch.Render(buf, 480, new RenderTiming(1.004, 0)); // 4 ms after the last packet ended
        Assert.Equal(24000 + 192 - 2400, ch.LastBlockStartPosition, 6);
    }

    [Fact]
    public void Offset_change_jumps_read_pointer_with_crossfade()
    {
        var (history, _, ch) = Create();
        var buf = new float[960];
        Feed(history, 1.0, 0.5);
        ch.Render(buf, 480, new RenderTiming(1.0, 480));
        var first = ch.LastBlockStartPosition;

        Feed(history, 1.01, 0.01);
        ch.OffsetMs = 100;
        ch.Render(buf, 480, new RenderTiming(1.01, 480));
        Assert.Equal(1, ch.GetStats().Jumps);
        Assert.Equal(first + 480 - 4800, ch.LastBlockStartPosition, 0);
    }

    [Fact]
    public void Source_pause_is_not_an_underrun()
    {
        var (history, _, ch) = Create();
        var buf = new float[960];
        Feed(history, 1.0, 0.2);
        ch.Render(buf, 480, new RenderTiming(1.0, 480));

        // No more packets: keep rendering for 0.5 s.
        for (var k = 1; k <= 50; k++)
        {
            ch.Render(buf, 480, new RenderTiming(1.0 + (k * 0.01), 480));
        }

        var stats = ch.GetStats();
        Assert.Equal(0, stats.Underruns);
        Assert.Equal(1, stats.SourceGaps);
        Assert.Equal(ChannelState.WaitingForAudio, stats.State);
    }

    [Fact]
    public void Starvation_while_source_is_active_counts_an_underrun()
    {
        var (history, shared, ch) = Create(baseLatencyMs: 20);
        var buf = new float[2880];
        Feed(history, 1.0, 0.2);
        ch.Render(buf, 480, new RenderTiming(1.0, 0));

        // The source just wrote (t=1.0), but the device asks for far more than the 20 ms buffered.
        Feed(history, 1.005, 0.005);
        ch.Render(buf, 1440, new RenderTiming(1.005, 0));
        Assert.Equal(ChannelState.Underrun, ch.GetStats().State);

        // Audio keeps flowing: the starvation is classified as a genuine underrun when playback resumes.
        Feed(history, 1.035, 0.03);
        ch.Render(buf, 480, new RenderTiming(1.035, 0));
        Assert.Equal(1, ch.GetStats().Underruns);
        Assert.Equal(0, ch.GetStats().SourceGaps);
        Assert.Equal(20, shared.BaseLatencyMs);
    }

    [Fact]
    public void Pass_window_mutes_positions_outside_it()
    {
        var (history, _, ch) = Create();
        Feed(history, 1.0, 0.5);
        var buf = new float[960];
        ch.SetPassWindow(0, 10); // long before the render position
        ch.Render(buf, 480, new RenderTiming(1.0, 480));
        Assert.All(buf, v => Assert.Equal(0f, v));

        ch.ClearPassWindow();
        Feed(history, 1.01, 0.01);
        ch.Render(buf, 480, new RenderTiming(1.01, 480));
        Assert.Contains(buf, v => Math.Abs(v) > 0.1f);
    }

    [Fact]
    public void Gain_and_master_gain_scale_output()
    {
        var (history, shared, ch) = Create();
        var buf = new float[960];
        Feed(history, 1.0, 0.5);
        ch.Gain = 0.5f;
        shared.MasterGain = 0.5f;
        for (var k = 0; k < 3; k++)
        {
            Feed(history, 1.0 + (k * 0.01), 0.01);
            ch.Render(buf, 480, new RenderTiming(1.0 + (k * 0.01), 480));
        }

        var peak = buf.Max(Math.Abs);
        Assert.InRange(peak, 0.120, 0.130); // 0.5 amplitude × 0.25
    }

    [Fact]
    public void Render_does_not_allocate()
    {
        var (history, _, ch) = Create();
        var buf = new float[960];
        var packet = new float[960];
        history.Reset(Rate);
        var t = 0.0;
        for (var i = 0; i < 30; i++)
        {
            history.Write(packet, t);
            t += 0.01;
        }

        ch.Render(buf, 480, new RenderTiming(t, 480));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2000; i++)
        {
            history.Write(packet, t);
            t += 0.01;
            ch.OffsetMs = (i / 100) % 2 == 0 ? 0 : 40; // forces jumps too
            ch.Render(buf, 480, new RenderTiming(t, 480));
            _ = ch.GetStats();
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(ch.GetStats().Jumps > 0);
    }
}
