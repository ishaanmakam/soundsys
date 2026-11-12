using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Simulation;
using Soundsys.Engine.Sync;
using Soundsys.Engine.Tests.Simulation;
using Xunit.Abstractions;

namespace Soundsys.Engine.Tests;

public class EngineTests(ITestOutputHelper output)
{
    private const string Desk = "sim:jbl-flip-6";
    private const string Bed = "sim:jbl-charge-5";
    private const string IPhone = "sim:phone-iphone";
    private const string Pixel = "sim:phone-pixel";

    private sealed class Rig : IDisposable
    {
        public Rig(AppSettings? settings = null, InMemorySettingsStore? store = null)
        {
            Scheduler = new SimScheduler();
            Backend = new SimulatedBackend(Scheduler);
            Store = store ?? new InMemorySettingsStore(settings);
            Power = new FakePower();
            Engine = new SyncEngine(Backend, Scheduler, Store, Power);
        }

        public SimScheduler Scheduler { get; }
        public SimulatedBackend Backend { get; }
        public InMemorySettingsStore Store { get; }
        public FakePower Power { get; }
        public SyncEngine Engine { get; }

        public void Run(double seconds) => Scheduler.RunFor(seconds);

        public T Await<T>(Task<T> task) => Drive(Scheduler, task, Engine);

        public void Dispose() => Engine.Dispose();
    }

    /// <summary>
    /// Steps virtual time until the task completes (analysis may finish on the thread pool). Runs without xUnit's
    /// SynchronizationContext so engine continuations triggered by simulated timers execute inline on this thread,
    /// exactly as they do on the app's timer/simulation threads; otherwise they'd be queued to the pool and virtual
    /// time would race ahead of them.
    /// </summary>
    private static T Drive<T>(SimScheduler scheduler, Task<T> task, SyncEngine? engine = null, double maxSeconds = 30)
    {
        var end = scheduler.Now + maxSeconds;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            while (!task.IsCompleted && scheduler.Now < end)
            {
                scheduler.RunFor(0.01);
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        if (!task.IsCompleted && !task.Wait(TimeSpan.FromSeconds(10)))
        {
            var st = engine?.GetStatus();
            throw new TimeoutException($"Task not finished after {maxSeconds} virtual s (scheduler now {scheduler.Now:F3}, state {st?.State}, step '{st?.CalibrationStep}', task {task.Status}).");
        }

        return task.GetAwaiter().GetResult();
    }

    private sealed class FakePower : IPowerManager
    {
        public bool KeepAwake { get; private set; }

        public void SetKeepAwake(bool keepAwake) => KeepAwake = keepAwake;
    }

    [Fact]
    public void Preselects_the_two_JBL_speakers_as_Desk_and_Bed()
    {
        using var rig = new Rig();
        Assert.Equal(Desk, rig.Engine.Settings.SpeakerAId);
        Assert.Equal(Bed, rig.Engine.Settings.SpeakerBId);
        var status = rig.Engine.GetStatus();
        Assert.Equal("Desk", status.Speakers[0].Name);
        Assert.Equal("Bed", status.Speakers[1].Name);
        Assert.Equal("Ready", status.Headline);
        Assert.Equal(80, status.Speakers[0].BatteryPercent);
    }

    [Fact]
    public void Laptop_mode_plays_on_both_speakers_and_keeps_the_laptop_awake()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop });
        Assert.True(rig.Engine.Start());
        Assert.Equal(EngineState.Running, rig.Engine.GetStatus().State); // reflected immediately
        rig.Run(1.0);

        var status = rig.Engine.GetStatus();
        output.WriteLine(status.Headline);
        Assert.True(status.AudioFlowing);
        Assert.All(status.Speakers, s => Assert.True(s.Playing));
        Assert.Equal("Playing this laptop's sound on Desk + Bed", status.Headline);
        Assert.True(rig.Power.KeepAwake);

        rig.Engine.Stop();
        Assert.False(rig.Power.KeepAwake);
        Assert.Equal(EngineState.Stopped, rig.Engine.GetStatus().State);
    }

    [Fact]
    public void Phone_mode_waits_for_a_phone_then_most_recent_phone_wins()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        rig.Run(1.0);
        Assert.Equal("Waiting for a phone", rig.Engine.GetStatus().Headline);

        rig.Backend.Phones.SimulatePlay(IPhone);
        rig.Run(1.0);
        var status = rig.Engine.GetStatus();
        output.WriteLine(status.Headline);
        Assert.Equal("Playing from Ishaan's iPhone on Desk + Bed", status.Headline);

        rig.Backend.Phones.SimulatePlay(Pixel);
        rig.Run(1.0);
        status = rig.Engine.GetStatus();
        output.WriteLine(status.Headline);
        Assert.Equal("Playing from Pixel 8 on Desk + Bed", status.Headline);
        Assert.Equal(PhoneLinkState.Waiting, rig.Backend.Phones.State(IPhone)); // released
    }

    [Fact]
    public void Disabled_phone_is_not_offered_to()
    {
        using var rig = new Rig();
        rig.Engine.SetPhoneEnabled(Pixel, false);
        rig.Engine.Start();
        rig.Run(0.5);
        Assert.Equal(PhoneLinkState.Disabled, rig.Backend.Phones.State(Pixel));
        Assert.False(rig.Engine.GetPhones().Single(p => p.Phone.Id == Pixel).Enabled);
    }

    [Fact]
    public void Paused_phone_shows_connected_and_press_play()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        rig.Backend.Phones.SimulatePlay(IPhone);
        rig.Run(1);
        rig.Backend.Phones.SimulatePause(IPhone);
        rig.Run(1);
        var status = rig.Engine.GetStatus();
        Assert.Equal("Connected to Ishaan's iPhone", status.Headline);
        Assert.Equal("Press play on the phone.", status.Detail);
    }

    [Fact]
    public void Speaker_drop_and_return_rejoins_quickly_aligned_without_disturbing_the_other_speaker()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop });
        rig.Engine.SetManualSync(-59); // Bed (241 ms) is 59 ms slower than Desk (182 ms): delay Desk
        var recorders = new Dictionary<string, BlockRecorder>();
        rig.Backend.OutputCreated += (_, device) =>
        {
            var slot = device.Id == Desk ? SpeakerSlot.A : SpeakerSlot.B;
            if (!recorders.TryGetValue(device.Id, out var rec))
            {
                rec = new BlockRecorder(rig.Engine.ChannelFor(slot), device.Options.AcousticLatencyMs / 1000);
                recorders[device.Id] = rec;
            }

            device.AddObserver(rec);
        };
        SimulatedOutputDevice? deskDevice = null;
        rig.Backend.OutputCreated += (_, d) => deskDevice ??= d.Id == Desk ? d : null;

        rig.Engine.Start();
        rig.Run(10);
        var deskBefore = rig.Engine.ChannelFor(SpeakerSlot.A).GetStats();

        rig.Backend.SetSpeakerAvailable(Bed, false); // Bed powered off at t≈10 s
        rig.Run(1.0);
        Assert.Equal(SpeakerLinkState.Lost, rig.Engine.GetStatus().Speakers[1].Link); // reflected within 1 s
        rig.Run(3.0);
        var during = rig.Engine.GetStatus();
        output.WriteLine($"During outage: {during.Headline} — {during.Detail}");
        Assert.Equal("Bed is off or out of range", during.Detail);
        Assert.Equal("Playing this laptop's sound on Desk", during.Headline);

        var back = rig.Scheduler.Now;
        rig.Backend.SetSpeakerAvailable(Bed, true); // Bed back on at t≈14 s
        double? rejoinedAt = null;
        while (rig.Scheduler.Now < back + 10)
        {
            rig.Run(0.05);
            if (rig.Engine.GetStatus().Speakers[1].Playing)
            {
                rejoinedAt = rig.Scheduler.Now;
                break;
            }
        }

        Assert.NotNull(rejoinedAt);
        var rejoin = rejoinedAt!.Value - back;
        output.WriteLine($"Bed playing again {rejoin * 1000:F0} ms after it came back (outage {rig.Engine.GetStatus().Speakers[1].LastOutageSeconds:F2} s)");
        Assert.True(rejoin < 3.0, $"rejoin took {rejoin:F2} s");

        rig.Run(20);
        var deskAfter = rig.Engine.ChannelFor(SpeakerSlot.A).GetStats();
        Assert.Equal(0, deskDevice!.Glitches);
        Assert.Equal(deskBefore.Underruns, deskAfter.Underruns);
        Assert.Equal(deskBefore.Jumps, deskAfter.Jumps);

        // Acoustic alignment after the rejoin (saved offset reapplied).
        var a = recorders[Desk];
        var b = recorders[Bed];
        double max = 0;
        var n = 0;
        for (var i = 0; i < b.HeardAt.Count; i++)
        {
            var t = b.HeardAt[i];
            if (t < rejoinedAt.Value + 1.5)
            {
                continue;
            }

            var pa = a.PositionAt(t);
            if (double.IsNaN(pa))
            {
                continue;
            }

            max = Math.Max(max, Math.Abs(pa - b.Position[i]) / 48000 * 1000);
            n++;
        }

        output.WriteLine($"Post-rejoin alignment: max |err| {max:F4} ms over {n} blocks");
        Assert.True(n > 1000);
        Assert.True(max < 0.5, $"max {max:F4} ms");
    }

    [Fact]
    public void Auto_calibration_in_the_simulated_room_finds_the_latency_difference()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop });
        rig.Engine.Start();
        rig.Run(2);
        var report = rig.Await(rig.Engine.CalibrateAsync());
        output.WriteLine($"{report.Message} A {report.LatencyAMs:F2} ms, B {report.LatencyBMs:F2} ms, rel {report.RelativeMs:F2} ms, quality {report.QualityADb:F0}/{report.QualityBDb:F0} dB; offsets {report.OffsetAMs}/{report.OffsetBMs}");

        Assert.True(report.Success, report.Message);
        Assert.InRange(report.RelativeMs!.Value, 59 - 2, 59 + 2); // 241 − 182 ms acoustic difference
        Assert.InRange(report.OffsetAMs, 57, 61);
        Assert.Equal(0, report.OffsetBMs);
        Assert.Equal(EngineState.Running, rig.Engine.GetStatus().State);
        Assert.Equal("auto", rig.Engine.Settings.Calibrations.Values.Single().Method);
        Assert.InRange(rig.Engine.GetStatus().SyncMs, -61, -57);

        // Re-calibrating with the offsets applied measures ≈ 0 ms.
        rig.Run(1);
        var again = rig.Await(rig.Engine.CalibrateAsync());
        output.WriteLine($"Second pass: rel {again.RelativeMs:F2} ms — {again.Message}");
        Assert.True(again.Success);
        Assert.InRange(again.RelativeMs!.Value, -2, 2);
    }

    [Fact]
    public void Calibration_reports_a_silent_speaker_in_plain_english()
    {
        var world = new SimWorld();
        var bed = world.Speakers.FindIndex(s => s.Options.Id == Bed);
        world.Speakers[bed] = (world.Speakers[bed].Options with { GainAtMic = 0 }, true, 45);
        var scheduler = new SimScheduler();
        var backend = new SimulatedBackend(scheduler, world);
        using var engine = new SyncEngine(backend, scheduler, new InMemorySettingsStore());
        engine.Start();
        scheduler.RunFor(1);
        var report = Drive(scheduler, engine.CalibrateAsync(), engine);
        output.WriteLine(report.Message);
        Assert.False(report.Success);
        Assert.Equal("Couldn't hear the Bed speaker. Turn it up or move the laptop closer, then try again.", report.Message);
        Assert.Equal(EngineState.Running, engine.GetStatus().State);
    }

    [Fact]
    public void Calibrating_while_stopped_explains_what_to_do()
    {
        using var rig = new Rig();
        var report = rig.Await(rig.Engine.CalibrateAsync());
        Assert.False(report.Success);
        Assert.Equal("Press Start first, then calibrate.", report.Message);
    }

    [Fact]
    public void Starting_with_a_speaker_off_plays_on_the_other_and_says_why()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop });
        rig.Backend.SetSpeakerAvailable(Bed, false);
        rig.Engine.Start();
        rig.Run(4);
        var status = rig.Engine.GetStatus();
        output.WriteLine($"{status.Headline} — {status.Detail}");
        Assert.True(status.Speakers[0].Playing);
        Assert.Equal("Bed is off or out of range", status.Speakers[1].Problem);
        Assert.Equal("Playing this laptop's sound on Desk", status.Headline);
    }

    [Fact]
    public void Starting_without_speakers_gives_a_plain_error()
    {
        var world = new SimWorld();
        world.Speakers.RemoveAll(s => s.Options.IsBluetooth);
        var scheduler = new SimScheduler();
        using var engine = new SyncEngine(new SimulatedBackend(scheduler, world), scheduler, new InMemorySettingsStore());
        Assert.False(engine.Start());
        var status = engine.GetStatus();
        Assert.Equal("Choose two speakers first (Speakers tab).", status.Error);
        Assert.Equal("Choose two speakers to get started", status.Headline);
    }

    [Fact]
    public void Auto_start_when_both_speakers_come_on()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop, AutoStartWhenSpeakersOn = true });
        rig.Backend.SetSpeakerAvailable(Bed, false);
        rig.Run(0.5);
        Assert.Equal(EngineState.Stopped, rig.Engine.GetStatus().State);
        rig.Backend.SetSpeakerAvailable(Bed, true);
        rig.Run(0.5);
        Assert.Equal(EngineState.Running, rig.Engine.GetStatus().State);

        // After the user stops, it doesn't restart on its own until a speaker has been off and on again.
        rig.Engine.Stop();
        rig.Backend.SetSpeakerAvailable(Desk, true);
        rig.Run(0.5);
        Assert.Equal(EngineState.Stopped, rig.Engine.GetStatus().State);
    }

    [Fact]
    public void Settings_persist_per_speaker_pair()
    {
        var store = new InMemorySettingsStore();
        using (var rig = new Rig(store: store))
        {
            rig.Engine.SetManualSync(30);
            rig.Engine.SetMasterVolume(0.5);
            rig.Engine.SetSpeakerVolume(SpeakerSlot.B, 0.25);
            rig.Engine.RenameSpeaker(Bed, "Bedroom");
            rig.Run(0.5);
        }

        using var again = new Rig(store: store);
        var status = again.Engine.GetStatus();
        Assert.Equal(30, again.Engine.GetManualSync());
        Assert.Equal(0.5, status.MasterVolume);
        Assert.Equal(0.25, status.Speakers[1].Volume);
        Assert.Equal("Bedroom", status.Speakers[1].Name);

        // The same pair in the opposite slots still finds its calibration.
        again.Engine.SelectSpeaker(SpeakerSlot.A, Bed);
        Assert.Equal(Bed, again.Engine.Settings.SpeakerAId);
        Assert.Equal(Desk, again.Engine.Settings.SpeakerBId);
        Assert.Equal(-30, again.Engine.GetManualSync());
    }

    [Fact]
    public void Follows_a_speaker_whose_endpoint_id_changes_keeping_name_volume_and_sync()
    {
        using var rig = new Rig();
        rig.Engine.SetManualSync(42);
        rig.Engine.SetSpeakerVolume(SpeakerSlot.B, 0.3);
        rig.Engine.RenameSpeaker(Bed, "Bedroom");
        rig.Run(0.5);

        rig.Backend.ReplaceSpeakerEndpoint(Bed, "sim:jbl-charge-5-new-stack");
        rig.Run(0.5);

        var status = rig.Engine.GetStatus();
        Assert.Equal("sim:jbl-charge-5-new-stack", rig.Engine.Settings.SpeakerBId);
        Assert.Equal("Bedroom", status.Speakers[1].Name);
        Assert.Equal(0.3, status.Speakers[1].Volume);
        Assert.Equal(42, rig.Engine.GetManualSync());
    }

    [Fact]
    public void Warns_when_windows_plays_straight_to_a_chosen_speaker()
    {
        var world = new SimWorld { DefaultSpeakerId = Desk };
        var scheduler = new SimScheduler();
        using var engine = new SyncEngine(new SimulatedBackend(scheduler, world), scheduler, new InMemorySettingsStore());
        Assert.StartsWith("Windows is also sending sound straight to Desk", engine.GetStatus().Warning);
    }

    [Fact]
    public void Switching_sources_does_not_disturb_speaker_alignment()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop });
        rig.Engine.SetManualSync(-59);
        var recorders = new Dictionary<string, BlockRecorder>();
        rig.Backend.OutputCreated += (_, device) =>
        {
            var rec = new BlockRecorder(rig.Engine.ChannelFor(device.Id == Desk ? SpeakerSlot.A : SpeakerSlot.B), device.Options.AcousticLatencyMs / 1000);
            recorders[device.Id] = rec;
            device.AddObserver(rec);
        };
        rig.Engine.Start();
        rig.Run(5);
        for (var k = 0; k < 4; k++)
        {
            rig.Engine.StartTestSignal(new MusicLikeSignal(), "Test music");
            rig.Run(1.5);
            rig.Engine.StopTestSignal();
            rig.Run(1.5);
        }

        var a = recorders[Desk];
        var b = recorders[Bed];
        double max = 0;
        for (var i = 0; i < b.HeardAt.Count; i++)
        {
            var pa = a.PositionAt(b.HeardAt[i]);
            if (b.HeardAt[i] > 3 && !double.IsNaN(pa))
            {
                max = Math.Max(max, Math.Abs(pa - b.Position[i]) / 48000 * 1000);
            }
        }

        var diag = rig.Engine.GetDiagnostics();
        output.WriteLine($"Across 8 source switches: max |err| {max:F4} ms; jumps {diag.A.Jumps}/{diag.B.Jumps}; corrections {diag.A.CorrectionPpm:F0}/{diag.B.CorrectionPpm:F0} ppm");
        Assert.True(max < 0.25, $"max {max:F4} ms");
        Assert.InRange(diag.A.CorrectionPpm, -100, 100); // nothing saturated
    }

    [Fact]
    public void Click_track_replaces_the_source_and_restores_it()
    {
        using var rig = new Rig(new AppSettings { Mode = SourceMode.Laptop });
        rig.Engine.Start();
        rig.Run(1);
        rig.Engine.StartClickTrack();
        rig.Run(1);
        Assert.Equal("Playing the click track on Desk + Bed", rig.Engine.GetStatus().Headline);
        rig.Engine.StopClickTrack();
        rig.Run(1);
        Assert.Equal("Playing this laptop's sound on Desk + Bed", rig.Engine.GetStatus().Headline);
    }
}
