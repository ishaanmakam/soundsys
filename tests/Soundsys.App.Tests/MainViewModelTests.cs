using Soundsys.App.ViewModels;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Settings;

namespace Soundsys.App.Tests;

/// <summary>Headline / ring / tray mapping for every state in docs/UI_DESIGN.md, against the real engine in simulation.</summary>
public class MainViewModelTests
{
    [Fact]
    public void Ready_shows_both_speakers_apart_and_outlined()
    {
        using var rig = new Rig();
        var vm = rig.Vm;
        Assert.Equal("Ready", vm.Headline);
        Assert.Equal("Desk + Bed", vm.Detail);
        Assert.Equal(RingVisual.Idle, vm.RingA);
        Assert.Equal(RingVisual.Idle, vm.RingB);
        Assert.False(vm.IsMerged);
        Assert.Equal("Start", vm.ActionCaption);
        Assert.True(vm.CanToggleRun);
        Assert.False(vm.IsEmpty);
        Assert.Equal(TrayVisual.Stopped, vm.Tray);
        Assert.Equal("Desk", vm.SpeakerA.Name);
        Assert.Equal("JBL Flip 6", vm.SpeakerA.DeviceText);
        Assert.Equal("80%", vm.SpeakerA.BatteryText);
        Assert.Equal(SpeakerDot.Idle, vm.SpeakerA.Dot);
        Assert.Null(vm.ErrorText);
        Assert.Null(vm.WarningText);
    }

    [Fact]
    public void Empty_state_asks_for_speakers_and_disables_the_button()
    {
        using var rig = new Rig(empty: true);
        var vm = rig.Vm;
        Assert.Equal("Choose two speakers to get started", vm.Headline);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.CanToggleRun);
        Assert.Equal(RingVisual.Off, vm.RingA);
        Assert.Equal(RingVisual.Off, vm.RingB);
        Assert.Equal("Not chosen", vm.SpeakerA.DeviceText);

        vm.ShowTabCommand.Execute(AppTab.Speakers);
        Assert.Equal(AppTab.Speakers, vm.SelectedTab);
    }

    [Fact]
    public void Start_moves_the_rings_together_immediately_then_plays()
    {
        using var rig = new Rig();
        rig.Engine.SetMode(SourceMode.Laptop);
        var vm = rig.Vm;

        var task = vm.ToggleRunCommand.ExecuteAsync(null);
        Assert.True(vm.IsMerged); // optimistic, before the engine call finishes
        Assert.Equal("Stop", vm.ActionCaption);
        rig.Await(task);
        Assert.Equal(EngineState.Running, rig.Engine.GetStatus().State);

        rig.RunUntil(() => vm.RingA == RingVisual.Playing && vm.RingB == RingVisual.Playing, what: "both playing");
        Assert.Equal("Playing this laptop's sound on Desk + Bed", vm.Headline);
        Assert.True(vm.IsMerged);
        Assert.Equal(TrayVisual.Playing, vm.Tray);
        Assert.Equal(SpeakerDot.Playing, vm.SpeakerA.Dot);
        Assert.True(vm.TrayText.Length <= MainViewModel.TooltipMax);

        rig.Await(vm.ToggleRunCommand.ExecuteAsync(null));
        Assert.Equal(EngineState.Stopped, rig.Engine.GetStatus().State);
        Assert.Equal("Ready", vm.Headline);
        Assert.False(vm.IsMerged);
        Assert.Equal("Start", vm.ActionCaption);
    }

    [Fact]
    public void Playing_from_a_phone_names_the_phone()
    {
        using var rig = new Rig();
        rig.StartPhonePlaying();
        var vm = rig.Vm;
        rig.RunUntil(() => vm.RingA == RingVisual.Playing, what: "playing");
        Assert.Equal("Playing from Ishaan's iPhone on Desk + Bed", vm.Headline);
        Assert.True(vm.IsPhoneMode);
        Assert.False(vm.IsLaptopMode);

        vm.PhonesPage.Refresh();
        var iphone = vm.PhonesPage.Phones.Single(p => p.Id == Rig.IPhone);
        Assert.Equal("Playing", iphone.StateText);
        Assert.True(iphone.IsPlaying);
        Assert.Equal("Not connected", vm.PhonesPage.Phones.Single(p => p.Id == Rig.Pixel).StateText);
    }

    [Fact]
    public void Waiting_for_a_phone_merges_the_rings_without_amber_fill()
    {
        using var rig = new Rig();
        Assert.True(rig.Engine.Start());
        rig.Run(1);
        var vm = rig.Vm;
        Assert.Equal("Waiting for a phone", vm.Headline);
        Assert.Contains("Bluetooth", vm.Detail);
        Assert.True(vm.IsMerged);
        Assert.Equal(RingVisual.Waiting, vm.RingA);
        Assert.Equal(RingVisual.Waiting, vm.RingB);
        Assert.Equal(TrayVisual.Stopped, vm.Tray);
    }

    [Fact]
    public void Connecting_breathes_then_says_the_speakers_are_off()
    {
        using var rig = new Rig();
        rig.Backend.SetSpeakerAvailable(Rig.Desk, false);
        rig.Backend.SetSpeakerAvailable(Rig.Bed, false);
        rig.Run(0.3);
        Assert.True(rig.Engine.Start());
        rig.Run(0.3);
        var vm = rig.Vm;
        Assert.Equal("Looking for your speakers", vm.Headline);
        Assert.Equal(RingVisual.Connecting, vm.RingA);
        Assert.Equal(RingVisual.Connecting, vm.RingB);
        Assert.False(vm.DetailIsAlert);

        rig.Run(4);
        Assert.Equal(RingVisual.Lost, vm.RingA);
        Assert.Equal(RingVisual.Lost, vm.RingB);
        Assert.Equal("Desk and Bed are off or out of range", vm.Detail);
        Assert.True(vm.DetailIsAlert);
        Assert.Equal(TrayVisual.Alert, vm.Tray);
    }

    [Fact]
    public void Poll_shows_a_speaker_drop_within_one_second()
    {
        using var rig = new Rig();
        rig.StartLaptopMode();
        var vm = rig.Vm;
        rig.RunUntil(() => vm.RingB == RingVisual.Playing, what: "Bed playing");

        rig.Backend.SetSpeakerAvailable(Rig.Bed, false);
        var took = rig.RunUntil(() => vm.RingB != RingVisual.Playing && vm.SpeakerB.Dot != SpeakerDot.Playing, maxSeconds: 1, what: "drop shown");
        Assert.True(took <= 1.0, $"took {took:F2} s");
        Assert.Equal(RingVisual.Playing, vm.RingA);

        // After the grace period it is spelled out, in Alert.
        rig.Run(4);
        Assert.Equal(RingVisual.Lost, vm.RingB);
        Assert.Equal("Bed is off or out of range", vm.Detail);
        Assert.True(vm.DetailIsAlert);
        Assert.StartsWith("Playing this laptop's sound on Desk", vm.Headline);
        Assert.Equal(TrayVisual.Alert, vm.Tray);
        Assert.Equal(SpeakerDot.Lost, vm.SpeakerB.Dot);

        // It comes back.
        rig.Backend.SetSpeakerAvailable(Rig.Bed, true);
        rig.RunUntil(() => vm.RingB == RingVisual.Playing, what: "Bed back");
        Assert.False(vm.DetailIsAlert);
    }

    [Fact]
    public void Calibrating_shows_live_steps_disables_controls_and_reports_plainly()
    {
        using var rig = new Rig();
        rig.StartLaptopMode();
        var vm = rig.Vm;

        var task = vm.SyncPage.CalibrateCommand.ExecuteAsync(null);
        rig.RunUntil(() => vm.SyncPage.StepText?.Contains("sweep", StringComparison.Ordinal) == true, what: "sweep step");
        Assert.Equal("Calibrating…", vm.Headline);
        Assert.Equal("Calibrating…", vm.ActionCaption);
        Assert.Equal(RingVisual.Calibrating, vm.RingA);
        Assert.Equal(RingVisual.Calibrating, vm.RingB);
        Assert.False(vm.CanToggleRun);
        Assert.True(vm.IsCalibrating);
        Assert.False(vm.SyncPage.CanCalibrate);

        rig.Await(task, maxSeconds: 40);
        Assert.False(vm.SyncPage.IsCalibrating);
        Assert.NotNull(vm.SyncPage.ResultText);
        Assert.False(vm.SyncPage.ResultIsProblem, vm.SyncPage.ResultText);
        Assert.Matches(@"^Desk \d+ ms · Bed \d+ ms → ", vm.SyncPage.NumbersText!);
        Assert.Equal(EngineState.Running, rig.Engine.GetStatus().State);
    }

    [Fact]
    public void Calibrating_while_stopped_explains_what_to_do()
    {
        using var rig = new Rig();
        rig.Await(rig.Vm.SyncPage.CalibrateCommand.ExecuteAsync(null));
        Assert.True(rig.Vm.SyncPage.ResultIsProblem);
        Assert.Equal("Press Start first, then calibrate.", rig.Vm.SyncPage.ResultText);
        Assert.Null(rig.Vm.SyncPage.NumbersText);
    }

    [Fact]
    public void Error_strip_shows_plain_text_and_can_be_dismissed()
    {
        using var rig = new Rig(empty: true);
        var vm = rig.Vm;
        Assert.False(rig.Engine.Start());
        vm.Refresh();
        Assert.Equal("Choose two speakers first (Speakers tab).", vm.ErrorText);

        vm.DismissErrorCommand.Execute(null);
        Assert.Null(vm.ErrorText);
        rig.Settle(() => rig.Engine.GetStatus().Error is null, "engine error cleared");
        Assert.Null(vm.ErrorText);

        vm.SettingsPage.Developer.ShowSampleErrorCommand.Execute(null);
        Assert.Equal(DeveloperViewModel.SampleError, vm.ErrorText);
        vm.Refresh();
        Assert.Equal(DeveloperViewModel.SampleError, vm.ErrorText);
        vm.DismissErrorCommand.Execute(null);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public void Warning_strip_appears_when_Windows_default_output_is_a_chosen_speaker()
    {
        using var rig = new Rig();
        var vm = rig.Vm;
        Assert.Null(vm.WarningText);
        vm.SpeakersPage.SlotB.SelectedId = Rig.Laptop;
        rig.Settle(() => rig.Engine.Settings.SpeakerBId == Rig.Laptop, "speaker B changed");
        vm.Refresh();
        Assert.NotNull(vm.WarningText);
        Assert.Contains("sound settings", vm.WarningText, StringComparison.Ordinal);

        vm.OpenSoundSettingsCommand.Execute(null);
        Assert.Equal(new[] { "ms-settings:sound" }, rig.Opened);
    }

    [Fact]
    public void Unexpected_exceptions_become_plain_sentences()
    {
        var message = EngineRunner.Describe(new InvalidOperationException("Object reference at 0x7FF1 in Soundsys.Engine.Foo()"));
        Assert.Equal(EngineRunner.UnexpectedMessage, message);
        Assert.Equal("Bed is off or out of range.", EngineRunner.Describe(new AudioDeviceException("Bed is off or out of range.")));

        using var rig = new Rig();
        var vm = rig.Vm;
        rig.Await(vm.Runner.RunAsync(_ => throw new NullReferenceException("boom")));
        Assert.Equal(EngineRunner.UnexpectedMessage, vm.ErrorText);
        AssertPlain(vm.ErrorText!);

        vm.ReportUnexpected(new InvalidCastException("System.InvalidCastException: at Foo.Bar() in C:\\src\\x.cs:line 12"));
        AssertPlain(vm.ErrorText!);
    }

    [Fact]
    public void Tooltip_is_the_headline_cut_to_63_characters()
    {
        Assert.Equal("Ready", MainViewModel.Truncate("Ready", 63));
        var longText = new string('x', 100);
        var cut = MainViewModel.Truncate(longText, 63);
        Assert.Equal(63, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    internal static void AssertPlain(string text)
    {
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
    }
}
