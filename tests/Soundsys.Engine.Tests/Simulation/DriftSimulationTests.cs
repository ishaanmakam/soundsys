using System.Diagnostics;
using Soundsys.Engine.Simulation;
using Xunit.Abstractions;

namespace Soundsys.Engine.Tests.Simulation;

public class DriftSimulationTests(ITestOutputHelper output)
{
    private static SimSpeakerOptions Desk(double ppm) => new()
    {
        Id = "desk",
        Name = "Desk",
        Ppm = ppm,
        AcousticLatencyMs = 180,
        CallbackJitterMs = 2.0,
        Seed = 1,
    };

    private static SimSpeakerOptions Bed(double ppm) => new()
    {
        Id = "bed",
        Name = "Bed",
        Ppm = ppm,
        AcousticLatencyMs = 240,
        CallbackJitterMs = 2.0,
        Seed = 2,
    };

    [Fact]
    public void One_hour_with_drifting_clocks_stays_aligned_within_a_quarter_millisecond()
    {
        // Phone clock +20 ppm, Desk +50 ppm, Bed −50 ppm (100 ppm apart = 360 ms/hour if uncorrected).
        var rig = new SimRig(
            new SimSourceOptions { Ppm = 20, JitterMs = 3, Signal = new ConstantSignal() },
            Desk(+50),
            Bed(-50));
        var sw = Stopwatch.StartNew();
        rig.Start();
        rig.Scheduler.RunUntil(3600);
        sw.Stop();

        var settled = rig.Alignment(60, 3600);
        var first10Min = rig.Alignment(60, 600);
        var lastMin = rig.Alignment(3540, 3600);
        var a = rig.ChannelA.GetStats();
        var b = rig.ChannelB.GetStats();
        output.WriteLine($"Simulated 3600 s in {sw.Elapsed.TotalSeconds:F1} s wall-clock ({3600 / sw.Elapsed.TotalSeconds:F0}x real time)");
        output.WriteLine($"Alignment 60 s–1 h: {settled}");
        output.WriteLine($"Alignment 60 s–10 min: {first10Min}; last minute: {lastMin}");
        output.WriteLine($"Desk: correction {a.CorrectionPpm:F1} ppm, underruns {a.Underruns}, jumps {a.Jumps}, glitches {rig.DeviceA.Glitches}");
        output.WriteLine($"Bed:  correction {b.CorrectionPpm:F1} ppm, underruns {b.Underruns}, jumps {b.Jumps}, glitches {rig.DeviceB.Glitches}");

        Assert.True(settled.Samples > 300_000, "expected an hour of blocks");
        Assert.True(settled.MaxAbsErrorMs < 0.25, settled.ToString());
        Assert.Equal(0, settled.SilentSamples);
        Assert.Equal(0, a.Underruns + b.Underruns);
        Assert.Equal(0, a.Jumps + b.Jumps);
        Assert.Equal(0, rig.DeviceA.Glitches + rig.DeviceB.Glitches);

        // The controller learned each speaker's drift relative to the phone: c ≈ ppm(source) − ppm(speaker).
        Assert.InRange(a.CorrectionPpm, -35, -25);
        Assert.InRange(b.CorrectionPpm, 65, 75);
    }

    [Fact]
    public void Inbound_dropouts_and_a_speaker_stall_recover_alignment_without_disturbing_the_other_speaker()
    {
        var rig = new SimRig(
            new SimSourceOptions
            {
                Ppm = -30,
                JitterMs = 5,
                Signal = new ConstantSignal(),
                Dropouts = [(20.0, 0.03), (40.0, 0.12), (60.0, 0.4)],
            },
            Desk(+45),
            Bed(-45) with { Stalls = [(80.0, 0.06)] });
        rig.Start();
        rig.Scheduler.RunUntil(120);

        // Ignore one second after each event; everything else must be tight.
        bool Disturbed(double t) =>
            new[] { 20.0, 40.0, 60.0, 80.0 }.Any(e => t >= e && t < e + 1.5);
        var report = rig.Alignment(10, 120, Disturbed);
        output.WriteLine($"Alignment outside disturbances: {report}");
        for (var t0 = 10.0; t0 < 120; t0 += 5)
        {
            output.WriteLine($"  [{t0,5:F0}, {t0 + 5,5:F0}) {rig.Alignment(t0, t0 + 5, Disturbed)}");
        }

        output.WriteLine($"Desk stats {rig.ChannelA.GetStats()}");
        output.WriteLine($"Bed stats  {rig.ChannelB.GetStats()}");
        output.WriteLine($"Router gap frames filled: {rig.Router.GapFrames}");
        output.WriteLine($"Desk glitches {rig.DeviceA.Glitches}, Bed glitches {rig.DeviceB.Glitches}");

        Assert.True(report.MaxAbsErrorMs < 0.25, string.Join("
", Enumerable.Range(0, 22).Select(k => $"[{10 + (5 * k)}s] {rig.Alignment(10 + (5 * k), 15 + (5 * k), Disturbed)}")));
        Assert.True(rig.Router.GapFrames > 0.5 * 48000 * 0.55, "lost inbound audio should be replaced with silence");

        // Bed's 60 ms stall starves only Bed's endpoint; Desk never glitches.
        Assert.Equal(0, rig.DeviceA.Glitches);
        Assert.True(rig.DeviceB.Glitches >= 1);
    }

    [Fact]
    public void Sample_rate_conversion_44k1_source_to_48k_speakers_stays_aligned()
    {
        var rig = new SimRig(
            new SimSourceOptions { SampleRate = 44100, PacketFrames = 441, Ppm = 100, Signal = new ConstantSignal() },
            Desk(-20),
            Bed(+20));
        rig.Start();
        rig.Scheduler.RunUntil(180);

        var report = rig.Alignment(60, 180);
        output.WriteLine($"44.1 kHz source: {report}");
        Assert.True(report.MaxAbsErrorMs < 0.25, report.ToString());
        Assert.Equal(0, rig.ChannelA.GetStats().Underruns + rig.ChannelB.GetStats().Underruns);
    }

    [Fact]
    public void Pausing_the_source_resumes_aligned_without_counting_underruns()
    {
        var rig = new SimRig(new SimSourceOptions { Signal = new ConstantSignal() }, Desk(10), Bed(-10));
        rig.Start();
        rig.Scheduler.RunUntil(30);
        rig.Source.Stop();
        rig.Scheduler.RunUntil(35);
        rig.Source.Start(rig.Router);
        rig.Scheduler.RunUntil(60);

        var report = rig.Alignment(37, 60);
        output.WriteLine($"After pause/resume: {report}");
        Assert.True(report.MaxAbsErrorMs < 0.25, report.ToString());
        Assert.Equal(0, rig.ChannelA.GetStats().Underruns + rig.ChannelB.GetStats().Underruns);
        Assert.Equal(1, rig.ChannelA.GetStats().SourceGaps);
    }
}
