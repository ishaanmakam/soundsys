using Soundsys.App.ViewModels;
using Soundsys.Engine;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Updates;

namespace Soundsys.App.Tests;

/// <summary>Every control reaches the engine (or the shell), and the screen follows.</summary>
public class CommandTests
{
    [Fact]
    public void Source_switch_sets_the_mode()
    {
        using var rig = new Rig();
        var vm = rig.Vm;
        Assert.True(vm.IsPhoneMode);
        vm.IsLaptopMode = true;
        rig.Settle(() => rig.Engine.Settings.Mode == SourceMode.Laptop, "laptop mode");
        Assert.True(vm.IsLaptopMode);
        Assert.False(vm.IsPhoneMode);

        rig.Await(vm.SetModeCommand.ExecuteAsync(SourceMode.Phone));
        Assert.Equal(SourceMode.Phone, rig.Engine.Settings.Mode);
        Assert.True(vm.IsPhoneMode);
    }

    [Fact]
    public void Volume_sliders_set_master_and_speaker_volume()
    {
        using var rig = new Rig();
        var vm = rig.Vm;
        vm.MasterVolume = 42;
        rig.Settle(() => Math.Abs(rig.Engine.Settings.MasterVolume - 0.42) < 1e-9, "master volume");
        Assert.Equal(42, vm.MasterVolume);

        vm.SpeakerB.Volume = 65;
        rig.Settle(() => Math.Abs(rig.Engine.GetStatus().Speakers[1].Volume - 0.65) < 1e-9, "Bed volume");
        Assert.Equal(1.0, rig.Engine.GetStatus().Speakers[0].Volume);
    }

    [Fact]
    public void Manual_sync_slider_round_trips_through_the_engine()
    {
        using var rig = new Rig();
        var vm = rig.Vm;
        Assert.Equal("In step", vm.SyncPage.ManualSyncText);
        Assert.Equal("← Desk later", vm.SyncPage.LeftLabel);
        Assert.Equal("Bed later →", vm.SyncPage.RightLabel);

        vm.SyncPage.ManualSync = 59;
        rig.Settle(() => rig.Engine.GetManualSync() == 59, "manual +59");
        Assert.Equal("Bed waits 59 ms", vm.SyncPage.ManualSyncText);
        Assert.Equal("+59 ms", vm.SpeakerB.OffsetText);
        Assert.Null(vm.SpeakerA.OffsetText);

        vm.SyncPage.ManualSync = -120;
        rig.Settle(() => rig.Engine.GetManualSync() == -120, "manual -120");
        Assert.Equal("Desk waits 120 ms", vm.SyncPage.ManualSyncText);
        Assert.Equal("+120 ms", vm.SpeakerA.OffsetText);
    }

    [Fact]
    public void Click_track_toggle_starts_and_stops_it()
    {
        using var rig = new Rig();
        rig.StartLaptopMode();
        var vm = rig.Vm;
        Assert.True(vm.SyncPage.CanClickTrack);
        vm.SyncPage.ClickTrack = true;
        rig.Settle(() => rig.Engine.GetStatus().ClickTrack, "click track on");
        rig.Run(1);
        Assert.StartsWith("Playing the click track on", vm.Headline);

        vm.SyncPage.ClickTrack = false;
        rig.Settle(() => !rig.Engine.GetStatus().ClickTrack, "click track off");
    }

    [Fact]
    public void Speaker_pickers_list_bluetooth_first_and_mark_unavailable_ones_off()
    {
        using var rig = new Rig();
        var page = rig.Vm.SpeakersPage;
        Assert.Equal(Rig.Desk, page.SlotA.SelectedId);
        Assert.Equal(Rig.Bed, page.SlotB.SelectedId);
        Assert.Equal("Desk", page.SlotA.NameText);
        var go = page.Choices.Single(c => c.Name == "JBL Go 3");
        Assert.Equal("off", go.Note);
        Assert.Null(page.Choices.Single(c => c.Id == Rig.Desk).Note);
        Assert.True(page.Choices.TakeWhile(c => c.IsBluetooth).Count() >= 3);
        Assert.False(page.Choices.Last().IsBluetooth);
    }

    [Fact]
    public void Renaming_and_choosing_speakers_reach_the_engine()
    {
        using var rig = new Rig();
        var page = rig.Vm.SpeakersPage;
        page.SlotA.NameText = "Kitchen";
        rig.Settle(() => rig.Engine.DisplayName(Rig.Desk) == "Kitchen", "rename");
        Assert.Equal("Kitchen", rig.Vm.SpeakerA.Name);
        Assert.Equal("Kitchen + Bed", rig.Vm.Detail);

        page.SlotA.SelectedId = "sim:jbl-go-3";
        rig.Settle(() => rig.Engine.Settings.SpeakerAId == "sim:jbl-go-3", "pick Go 3");
        Assert.Equal("JBL Go 3", rig.Vm.SpeakerA.Name);
        Assert.Equal("off", rig.Vm.SpeakerA.DeviceText);
        Assert.Equal(SpeakerDot.Off, rig.Vm.SpeakerA.Dot);
        Assert.Equal(RingVisual.Off, rig.Vm.RingA);
    }

    [Fact]
    public void Phone_toggle_disables_the_phone()
    {
        using var rig = new Rig();
        var phones = rig.Vm.PhonesPage;
        Assert.True(phones.HasPhones);
        var pixel = phones.Phones.Single(p => p.Id == Rig.Pixel);
        Assert.True(pixel.Enabled);
        pixel.Enabled = false;
        Assert.Equal("Off", pixel.StateText);
        rig.Settle(() => rig.Engine.Settings.DisabledPhoneIds.Contains(Rig.Pixel), "pixel disabled");
        phones.Refresh();
        Assert.False(pixel.Enabled);
    }

    [Fact]
    public void Settings_toggles_reach_settings_and_startup_registration()
    {
        using var rig = new Rig();
        var s = rig.Vm.SettingsPage;
        Assert.False(s.StartWithWindows);
        s.StartWithWindows = true;
        rig.Settle(() => rig.Startup.IsEnabled && rig.Engine.Settings.StartWithWindows, "start with Windows");

        s.AutoStart = false;
        s.KeepAwake = false;
        s.CheckAutomatically = false;
        rig.Settle(() => !rig.Engine.Settings.AutoStartWhenSpeakersOn && !rig.Engine.Settings.KeepAwake && !rig.Engine.Settings.CheckForUpdates, "toggles");
        Assert.True(rig.Store.Saves > 0);
    }

    [Fact]
    public void Five_clicks_on_the_version_unlock_developer_options()
    {
        using var rig = new Rig(exposeSimulation: false);
        var s = rig.Vm.SettingsPage;
        Assert.Equal("Version 0.1.0-alpha", s.VersionText);
        Assert.False(s.DeveloperUnlocked);
        for (var i = 0; i < SettingsViewModel.UnlockClicks - 1; i++)
        {
            s.VersionClickedCommand.Execute(null);
        }

        Assert.False(s.DeveloperUnlocked);
        s.VersionClickedCommand.Execute(null);
        Assert.True(s.DeveloperUnlocked);
        rig.Settle(() => rig.Engine.Settings.DeveloperOptionsUnlocked, "unlock saved");
        Assert.False(s.Developer.IsSimulated);
    }

    [Fact]
    public void Simulated_devices_switch_saves_and_offers_a_restart()
    {
        using var rig = new Rig();
        var dev = rig.Vm.SettingsPage.Developer;
        Assert.True(dev.IsSimulated);
        Assert.True(dev.UseSimulated); // running simulated
        Assert.False(dev.RestartNeeded);

        dev.UseSimulated = false;
        Assert.Equal(new[] { false }, rig.SavedSimulated);
        Assert.True(dev.RestartNeeded);
        dev.RestartCommand.Execute(null);
        Assert.Equal(new[] { false }, rig.Restarts);
    }

    [Fact]
    public void Developer_controls_drive_the_simulated_world()
    {
        using var rig = new Rig();
        var vm = rig.Vm;
        var dev = vm.SettingsPage.Developer;
        Assert.Equal(4, dev.SimSpeakers.Count);
        Assert.Equal(2, dev.SimPhones.Count);

        rig.StartLaptopMode();
        rig.RunUntil(() => vm.RingB == RingVisual.Playing, what: "playing");
        dev.SimSpeakers.Single(s => s.Id == Rig.Bed).IsOn = false;
        rig.Settle(() => !rig.Backend.IsSpeakerAvailable(Rig.Bed), "Bed off");
        rig.RunUntil(() => vm.RingB == RingVisual.Lost, what: "Bed lost");

        dev.LaptopPlaying = false;
        Assert.False(rig.Backend.LaptopPlaying);
        rig.RunUntil(() => vm.Headline == "Waiting for sound from this laptop", what: "laptop silent");

        rig.Await(vm.SetModeCommand.ExecuteAsync(SourceMode.Phone));
        rig.Run(0.5);
        var iphone = dev.SimPhones.Single(p => p.Name == "Ishaan's iPhone");
        iphone.PlayCommand.Execute(null);
        rig.RunUntil(() => vm.Headline.StartsWith("Playing from Ishaan's iPhone", StringComparison.Ordinal), what: "phone playing");
        iphone.PauseCommand.Execute(null);
        rig.RunUntil(() => vm.Headline == "Connected to Ishaan's iPhone", what: "phone paused");
        iphone.LeaveCommand.Execute(null);
        rig.RunUntil(() => vm.Headline == "Waiting for a phone", what: "phone left");

        rig.Await(dev.ForgetSpeakersCommand.ExecuteAsync(null));
        Assert.Equal("Choose two speakers to get started", vm.Headline);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Update_check_runs_on_startup_only_when_due()
    {
        var calls = 0;
        Task<UpdateCheckResult> Check(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new UpdateCheckResult(true, "0.2.0", "https://example.invalid/r", "soundsys 0.2.0 is available."));
        }

        var now = new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);

        using (var recent = new Rig(settings: new AppSettings { LastUpdateCheck = now.AddHours(-2) }, checkForUpdates: Check, now: now))
        {
            recent.Await(recent.Vm.SettingsPage.CheckOnStartupAsync());
            Assert.Equal(0, calls);
        }

        using (var off = new Rig(settings: new AppSettings { CheckForUpdates = false }, checkForUpdates: Check, now: now))
        {
            off.Await(off.Vm.SettingsPage.CheckOnStartupAsync());
            Assert.Equal(0, calls);
        }

        using var due = new Rig(settings: new AppSettings { LastUpdateCheck = now.AddHours(-25) }, checkForUpdates: Check, now: now);
        UpdateCheckResult? found = null;
        due.Vm.SettingsPage.UpdateFound += (_, r) => found = r;
        due.Await(due.Vm.SettingsPage.CheckOnStartupAsync());
        Assert.Equal(1, calls);
        Assert.Equal("soundsys 0.2.0 is available.", due.Vm.SettingsPage.UpdateResultText);
        Assert.Equal("https://example.invalid/r", due.Vm.SettingsPage.UpdateUrl);
        Assert.NotNull(found);
        due.Settle(() => due.Engine.Settings.LastUpdateCheck == now, "last check saved");

        due.Vm.SettingsPage.OpenUpdateCommand.Execute(null);
        Assert.Equal(new[] { "https://example.invalid/r" }, due.Opened);
    }

    [Fact]
    public void Manual_update_check_shows_the_result_text()
    {
        using var rig = new Rig(checkForUpdates: _ => Task.FromResult(new UpdateCheckResult(false, "0.1.0-alpha", null, "You're up to date (0.1.0-alpha).")));
        rig.Await(rig.Vm.SettingsPage.CheckUpdatesCommand.ExecuteAsync(null));
        Assert.Equal("You're up to date (0.1.0-alpha).", rig.Vm.SettingsPage.UpdateResultText);
        Assert.Null(rig.Vm.SettingsPage.UpdateUrl);
    }

    [Fact]
    public void Battery_glyph_tracks_the_level()
    {
        Assert.Equal("", SpeakerRowViewModel.BatteryGlyphFor(0));
        Assert.Equal("", SpeakerRowViewModel.BatteryGlyphFor(80));
        Assert.Equal("", SpeakerRowViewModel.BatteryGlyphFor(100));
    }

    [Fact]
    public void Ring_mapping_covers_every_link_state()
    {
        static SpeakerStatus S(Engine.Sync.SpeakerLinkState link, bool available = true, string? problem = null, string? id = "x") =>
            new(SpeakerSlot.A, id, "Desk", "JBL", link, available, false, null, 1, 0, 0, 0, 0, null, problem);

        Assert.Equal(RingVisual.Off, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Idle, id: null), EngineState.Stopped, false));
        Assert.Equal(RingVisual.Off, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Idle, available: false), EngineState.Stopped, false));
        Assert.Equal(RingVisual.Idle, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Idle), EngineState.Stopped, false));
        Assert.Equal(RingVisual.Connecting, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Connecting), EngineState.Running, true));
        Assert.Equal(RingVisual.Connecting, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Lost), EngineState.Running, true));
        Assert.Equal(RingVisual.Lost, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Lost, problem: "off"), EngineState.Running, true));
        Assert.Equal(RingVisual.Waiting, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Connected), EngineState.Running, false));
        Assert.Equal(RingVisual.Playing, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Connected), EngineState.Running, true));
        Assert.Equal(RingVisual.Calibrating, SpeakerRowViewModel.RingFor(S(Engine.Sync.SpeakerLinkState.Connected), EngineState.Calibrating, true));
    }
}
