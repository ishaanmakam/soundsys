using Soundsys.Engine.Dsp;
using Soundsys.Engine.Simulation;
using Soundsys.Engine.Sync;

namespace Soundsys.Engine.Tests.Simulation;

/// <summary>Cheap constant signal for long runs (alignment is measured from positions, not content).</summary>
internal sealed class ConstantSignal : ISimSignal
{
    public void Fill(Span<float> interleaved, int channels, long firstFrame, int sampleRate) => interleaved.Fill(0.01f);
}

/// <summary>Records, for every rendered block, when it is heard and which source position it starts at.</summary>
internal sealed class BlockRecorder(OutputChannel channel, double acousticLatencySeconds) : ISimPlaybackObserver
{
    public List<double> HeardAt { get; } = [];
    public List<double> Position { get; } = [];
    public List<double> PositionsPerSecond { get; } = [];

    public void OnPlayed(SimulatedOutputDevice device, double dacTime, double trueRate, ReadOnlySpan<float> interleaved)
    {
        var start = channel.LastBlockStartPosition;
        if (double.IsNaN(start))
        {
            return;
        }

        HeardAt.Add(dacTime + acousticLatencySeconds);
        Position.Add(start);
        PositionsPerSecond.Add(channel.LastBlockStep * trueRate);
    }

    /// <summary>Source position heard at acoustic time <paramref name="t"/>, or NaN if silent then.</summary>
    public double PositionAt(double t)
    {
        var i = HeardAt.BinarySearch(t);
        if (i < 0)
        {
            i = ~i - 1;
        }

        if (i < 0 || t - HeardAt[i] > 0.05)
        {
            return double.NaN;
        }

        return Position[i] + ((t - HeardAt[i]) * PositionsPerSecond[i]);
    }
}

internal sealed record AlignmentReport(double MaxAbsErrorMs, double MeanAbsErrorMs, int Samples, int SilentSamples)
{
    public override string ToString() => $"max |err| {MaxAbsErrorMs:F4} ms, mean |err| {MeanAbsErrorMs:F4} ms over {Samples} blocks ({SilentSamples} silent)";
}

/// <summary>source → router → shared history → two channels → two simulated speakers, on virtual time.</summary>
internal sealed class SimRig
{
    public SimRig(SimSourceOptions source, SimSpeakerOptions a, SimSpeakerOptions b, double baseLatencyMs = 50)
    {
        Scheduler = new SimScheduler();
        History = new HistoryBuffer(1 << 19);
        Shared = new SharedSyncSettings { BaseLatencyMs = baseLatencyMs };
        Router = new SourceRouter(History);
        Source = new SimulatedSource(Scheduler, source);
        DeviceA = new SimulatedOutputDevice(Scheduler, a);
        DeviceB = new SimulatedOutputDevice(Scheduler, b);
        ChannelA = new OutputChannel(History, Shared);
        ChannelB = new OutputChannel(History, Shared);

        // Offsets that compensate the speakers' acoustic latency difference (what calibration would find).
        var diff = b.AcousticLatencyMs - a.AcousticLatencyMs;
        ChannelA.OffsetMs = Math.Max(0, diff);
        ChannelB.OffsetMs = Math.Max(0, -diff);

        RecorderA = new BlockRecorder(ChannelA, a.AcousticLatencyMs / 1000);
        RecorderB = new BlockRecorder(ChannelB, b.AcousticLatencyMs / 1000);
        DeviceA.AddObserver(RecorderA);
        DeviceB.AddObserver(RecorderB);
        SourceRate = source.SampleRate;
    }

    public SimScheduler Scheduler { get; }
    public HistoryBuffer History { get; }
    public SharedSyncSettings Shared { get; }
    public SourceRouter Router { get; }
    public SimulatedSource Source { get; }
    public SimulatedOutputDevice DeviceA { get; }
    public SimulatedOutputDevice DeviceB { get; }
    public OutputChannel ChannelA { get; }
    public OutputChannel ChannelB { get; }
    public BlockRecorder RecorderA { get; }
    public BlockRecorder RecorderB { get; }
    public int SourceRate { get; }

    public void Start()
    {
        Source.Start(Router);
        DeviceA.Start(ChannelA);
        DeviceB.Start(ChannelB);
    }

    /// <summary>Compares what both speakers emit at the same acoustic instant, for B's blocks heard in [from, to).</summary>
    public AlignmentReport Alignment(double from, double to, Func<double, bool>? exclude = null)
    {
        double max = 0, sum = 0;
        int n = 0, silent = 0;
        for (var i = 0; i < RecorderB.HeardAt.Count; i++)
        {
            var t = RecorderB.HeardAt[i];
            if (t < from || t >= to || (exclude?.Invoke(t) ?? false))
            {
                continue;
            }

            var pa = RecorderA.PositionAt(t);
            if (double.IsNaN(pa))
            {
                silent++;
                continue;
            }

            var err = Math.Abs(pa - RecorderB.Position[i]) / SourceRate * 1000.0;
            max = Math.Max(max, err);
            sum += err;
            n++;
        }

        return new AlignmentReport(max, n == 0 ? double.NaN : sum / n, n, silent);
    }
}
