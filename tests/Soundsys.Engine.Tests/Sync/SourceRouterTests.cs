using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Dsp;
using Soundsys.Engine.Sync;

namespace Soundsys.Engine.Tests.Sync;

public class SourceRouterTests
{
    [Fact]
    public void Mono_is_duplicated_to_both_channels()
    {
        var history = new HistoryBuffer(1024);
        var router = new SourceRouter(history);
        router.OnFormat(new AudioFormat(48000, 1));
        router.Write([0.1f, 0.2f, 0.3f], 0, false);
        var l = new float[3];
        var r = new float[3];
        history.ReadDeinterleaved(0, l, r);
        Assert.Equal([0.1f, 0.2f, 0.3f], l);
        Assert.Equal(l, r);
    }

    [Fact]
    public void Multichannel_keeps_front_left_and_right()
    {
        var history = new HistoryBuffer(1024);
        var router = new SourceRouter(history);
        router.OnFormat(new AudioFormat(48000, 4));
        router.Write([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], 0, false);
        var l = new float[2];
        var r = new float[2];
        history.ReadDeinterleaved(0, l, r);
        Assert.Equal([1f, 5f], l);
        Assert.Equal([2f, 6f], r);
    }

    [Fact]
    public void Capture_gaps_are_filled_with_silence_to_keep_the_timeline()
    {
        var history = new HistoryBuffer(1 << 14);
        var router = new SourceRouter(history);
        router.OnFormat(new AudioFormat(48000, 2));
        var packet = new float[960];
        router.Write(packet, 0.00, false);
        router.Write(packet, 0.01, false);
        router.Write(packet, 0.05, true); // 30 ms lost
        Assert.Equal(480 * 3 + 1440, history.ReadState().Position);
        Assert.Equal(1440, router.GapFrames);
    }

    [Fact]
    public void Jitter_and_long_pauses_are_not_padded()
    {
        var history = new HistoryBuffer(1 << 14);
        var router = new SourceRouter(history);
        router.OnFormat(new AudioFormat(48000, 2));
        var packet = new float[960];
        router.Write(packet, 0.000, false);
        router.Write(packet, 0.0115, false); // 1.5 ms late: jitter
        router.Write(packet, 5.0, false); // resumed after a pause
        Assert.Equal(480 * 3, history.ReadState().Position);
        Assert.Equal(0, router.GapFrames);
    }

    [Fact]
    public void Sample_rate_change_starts_a_new_timeline()
    {
        var history = new HistoryBuffer(1024);
        var router = new SourceRouter(history);
        router.OnFormat(new AudioFormat(48000, 2));
        router.Write(new float[200], 0, false);
        var epoch = history.ReadState().Epoch;
        router.OnFormat(new AudioFormat(44100, 2));
        Assert.Equal(epoch + 1, history.ReadState().Epoch);
        Assert.Equal(44100, history.ReadState().SampleRate);

        router.OnFormat(new AudioFormat(44100, 1)); // same rate: timeline continues
        Assert.Equal(epoch + 1, history.ReadState().Epoch);
    }

    [Fact]
    public void Switching_sources_at_the_same_rate_keeps_the_timeline_continuous()
    {
        var history = new HistoryBuffer(1 << 14);
        var router = new SourceRouter(history);
        router.OnFormat(new AudioFormat(48000, 2));
        router.Write(new float[960], 0.00, false);       // old source ends at 0.01
        router.OnFormat(new AudioFormat(48000, 1));      // new source, same rate
        router.Write(new float[480], 0.05, false);       // starts 40 ms later
        Assert.Equal(480 + 1920 + 480, history.ReadState().Position);
        Assert.Equal(0.05 + 0.01, history.ReadState().EndTimestamp, 9);
    }

    [Fact]
    public void Level_tracks_signal()
    {
        var router = new SourceRouter(new HistoryBuffer(1 << 14));
        router.OnFormat(new AudioFormat(48000, 2));
        var loud = Enumerable.Repeat(0.5f, 960).ToArray();
        for (var i = 0; i < 20; i++)
        {
            router.Write(loud, i * 0.01, false);
        }

        Assert.InRange(router.Level, 0.45, 0.55);
    }
}
