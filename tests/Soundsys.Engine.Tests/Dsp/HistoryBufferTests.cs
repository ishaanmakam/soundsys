using Soundsys.Engine.Dsp;

namespace Soundsys.Engine.Tests.Dsp;

public class HistoryBufferTests
{
    private static float[] Ramp(int frames, int startFrame)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            data[2 * i] = startFrame + i;
            data[2 * i + 1] = -(startFrame + i);
        }

        return data;
    }

    [Fact]
    public void Capacity_must_be_power_of_two()
    {
        Assert.Throws<ArgumentException>(() => new HistoryBuffer(1000));
        Assert.Equal(1024, new HistoryBuffer(1024).CapacityFrames);
    }

    [Fact]
    public void Fresh_buffer_reports_no_source()
    {
        var buffer = new HistoryBuffer(1024);
        var state = buffer.ReadState();
        Assert.Equal(0, state.SampleRate);
        Assert.Equal(0, state.Position);
    }

    [Fact]
    public void Write_publishes_position_and_end_timestamp()
    {
        var buffer = new HistoryBuffer(1024);
        buffer.Reset(48000);
        buffer.Write(Ramp(480, 0), timestampSeconds: 10.0);

        var state = buffer.ReadState();
        Assert.Equal(480, state.Position);
        Assert.Equal(48000, state.SampleRate);
        Assert.Equal(10.0 + 480 / 48000.0, state.EndTimestamp, 9);
    }

    [Fact]
    public void Reads_back_across_wraparound()
    {
        var buffer = new HistoryBuffer(1024);
        buffer.Reset(48000);
        for (var k = 0; k < 5; k++)
        {
            buffer.Write(Ramp(300, k * 300), k * 0.01);
        }

        // Positions 1000..1400 straddle the 1024 wrap.
        var left = new float[400];
        var right = new float[400];
        buffer.ReadDeinterleaved(1000, left, right);
        for (var i = 0; i < 400; i++)
        {
            Assert.Equal(1000 + i, left[i]);
            Assert.Equal(-(1000 + i), right[i]);
        }
    }

    [Fact]
    public void Silence_fill_advances_timeline_with_zeros()
    {
        var buffer = new HistoryBuffer(1024);
        buffer.Reset(48000);
        buffer.Write(Ramp(100, 1), 0);
        buffer.WriteSilence(50, 1.0);
        Assert.Equal(150, buffer.ReadState().Position);

        var left = new float[50];
        var right = new float[50];
        buffer.ReadDeinterleaved(100, left, right);
        Assert.All(left, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Reset_bumps_epoch_and_clears_position()
    {
        var buffer = new HistoryBuffer(1024);
        buffer.Reset(48000);
        buffer.Write(Ramp(10, 0), 0);
        var epoch = buffer.ReadState().Epoch;
        buffer.Reset(44100);
        var state = buffer.ReadState();
        Assert.Equal(epoch + 1, state.Epoch);
        Assert.Equal(0, state.Position);
        Assert.Equal(44100, state.SampleRate);
    }

    [Fact]
    public void Writes_larger_than_capacity_keep_the_newest_frames()
    {
        var buffer = new HistoryBuffer(256);
        buffer.Reset(48000);
        buffer.Write(Ramp(1000, 0), 0);
        var left = new float[100];
        var right = new float[100];
        buffer.ReadDeinterleaved(900, left, right);
        Assert.Equal(900f, left[0]);
        Assert.Equal(999f, left[99]);
    }

    [Fact]
    public async Task Concurrent_reader_never_sees_torn_state()
    {
        var buffer = new HistoryBuffer(1 << 14);
        buffer.Reset(48000);
        using var cts = new CancellationTokenSource();
        var violations = 0;
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var s = buffer.ReadState();
                // Writer keeps EndTimestamp == Position / rate exactly; a torn read breaks the invariant.
                if (Math.Abs(s.EndTimestamp - s.Position / 48000.0) > 1e-9)
                {
                    Interlocked.Increment(ref violations);
                }
            }
        });

        var block = new float[64 * 2];
        for (var i = 0; i < 200_000; i++)
        {
            buffer.Write(block, i * 64 / 48000.0);
        }

        cts.Cancel();
        await reader;
        Assert.Equal(0, violations);
    }
}
