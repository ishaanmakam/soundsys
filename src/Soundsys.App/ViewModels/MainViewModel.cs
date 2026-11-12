using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundsys.Engine;
using Soundsys.Engine.Settings;

namespace Soundsys.App.ViewModels;

/// <summary>
/// Everything on screen. <see cref="Refresh"/> polls <see cref="SyncEngine.GetStatus"/> (the app calls it at 10 Hz);
/// commands run on the thread pool and update the screen optimistically so every action shows within a second.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const int TooltipMax = 63;

    private readonly SyncEngine _engine;
    private readonly AppServices _services;
    private readonly EditHold _masterHold = new();
    private bool? _pendingRun;
    private SourceMode? _pendingMode;
    private string? _localError;
    private string? _dismissedEngineError;
    private bool _applying;
    private int _tick;

    public MainViewModel(SyncEngine engine, AppServices services)
    {
        _engine = engine;
        _services = services;
        Runner = new EngineRunner(engine, ReportError);
        SpeakerA = new SpeakerRowViewModel(SpeakerSlot.A, Runner);
        SpeakerB = new SpeakerRowViewModel(SpeakerSlot.B, Runner);
        SpeakersPage = new SpeakersViewModel(Runner, this);
        PhonesPage = new PhonesViewModel(Runner);
        SyncPage = new SyncViewModel(Runner, this);
        SettingsPage = new SettingsViewModel(Runner, services, this);
        _engine.DevicesChanged += OnDevicesChanged;
        SpeakersPage.Reload();
        PhonesPage.Reload();
        Refresh();
    }

    public EngineRunner Runner { get; }

    public SyncEngine Engine => _engine;

    public SpeakerRowViewModel SpeakerA { get; }

    public SpeakerRowViewModel SpeakerB { get; }

    public SpeakersViewModel SpeakersPage { get; }

    public PhonesViewModel PhonesPage { get; }

    public SyncViewModel SyncPage { get; }

    public SettingsViewModel SettingsPage { get; }

    /// <summary>The last status the poll saw.</summary>
    public EngineStatus? Status { get; private set; }

    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    [ObservableProperty]
    public partial string? Detail { get; set; }

    /// <summary>Detail is a problem (a speaker is off or out of range): shown in Alert.</summary>
    [ObservableProperty]
    public partial bool DetailIsAlert { get; set; }

    /// <summary>No speakers chosen: the detail offers a link to the Speakers tab.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStrip))]
    public partial string? ErrorText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStrip))]
    public partial string? WarningText { get; set; }

    /// <summary>An error or notice strip is showing (Home tightens up so everything still fits).</summary>
    public bool HasStrip => !string.IsNullOrEmpty(ErrorText) || !string.IsNullOrEmpty(WarningText);

    [ObservableProperty]
    public partial RingVisual RingA { get; set; }

    [ObservableProperty]
    public partial RingVisual RingB { get; set; }

    [ObservableProperty]
    public partial bool IsMerged { get; set; }

    /// <summary>The verb under the rings: "Start", "Stop" or "Calibrating…".</summary>
    [ObservableProperty]
    public partial string ActionCaption { get; set; } = "Start";

    [ObservableProperty]
    public partial bool CanToggleRun { get; set; }

    /// <summary>Running or calibrating (or about to be).</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsCalibrating { get; set; }

    [ObservableProperty]
    public partial bool IsPhoneMode { get; set; } = true;

    [ObservableProperty]
    public partial bool IsLaptopMode { get; set; }

    /// <summary>0..100.</summary>
    [ObservableProperty]
    public partial double MasterVolume { get; set; } = 80;

    [ObservableProperty]
    public partial AppTab SelectedTab { get; set; }

    [ObservableProperty]
    public partial TrayVisual Tray { get; set; }

    /// <summary>Tray tooltip: the headline, at most 63 characters.</summary>
    [ObservableProperty]
    public partial string TrayText { get; set; } = "soundsys";

    public bool IsSimulated => _engine.IsSimulated;

    /// <summary>Polls the engine and maps its status to the screen. Call on the UI thread.</summary>
    public void Refresh()
    {
        EngineStatus st;
        try
        {
            st = _engine.GetStatus();
        }
        catch (Exception ex)
        {
            ReportError(EngineRunner.Describe(ex));
            return;
        }

        Status = st;
        _applying = true;
        try
        {
            Apply(st);
        }
        finally
        {
            _applying = false;
        }

        SyncPage.Apply(st);
        if (++_tick % 5 == 0)
        {
            PhonesPage.Refresh();
            SettingsPage.Developer.Refresh();
        }
    }

    /// <summary>Shows a plain-English error in the strip until dismissed.</summary>
    public void ReportError(string message)
    {
        _services.Post(() =>
        {
            _localError = message;
            ErrorText = message;
        });
    }

    /// <summary>Unexpected exception from anywhere in the app: log it, show a plain sentence.</summary>
    public void ReportUnexpected(Exception ex)
    {
        Trace.TraceError($"soundsys unexpected: {ex}");
        ReportError(EngineRunner.UnexpectedMessage);
    }

    public void Dispose() => _engine.DevicesChanged -= OnDevicesChanged;

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (IsCalibrating)
        {
            return;
        }

        var start = !IsRunning;
        _pendingRun = start;
        Refresh();
        try
        {
            await Runner.RunAsync(e =>
            {
                if (start)
                {
                    e.Start();
                }
                else
                {
                    e.Stop();
                }
            });
        }
        finally
        {
            _pendingRun = null;
            Refresh();
        }
    }

    /// <summary>Tray menu / keyboard: start if stopped, stop if running.</summary>
    public Task StartAsync() => IsRunning ? Task.CompletedTask : ToggleRunAsync();

    public Task StopAsync() => IsRunning ? ToggleRunAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task SetModeAsync(SourceMode mode) => ChangeModeAsync(mode);

    [RelayCommand]
    private void DismissError()
    {
        _localError = null;
        _dismissedEngineError = Status?.Error;
        ErrorText = null;
        Runner.Post(e => e.ClearError());
    }

    [RelayCommand]
    private void OpenSoundSettings() => _services.OpenUri("ms-settings:sound");

    [RelayCommand]
    private void ShowTab(AppTab tab) => SelectedTab = tab;

    partial void OnIsPhoneModeChanged(bool value)
    {
        if (!_applying && value)
        {
            _ = ChangeModeAsync(SourceMode.Phone);
        }
    }

    partial void OnIsLaptopModeChanged(bool value)
    {
        if (!_applying && value)
        {
            _ = ChangeModeAsync(SourceMode.Laptop);
        }
    }

    partial void OnMasterVolumeChanged(double value)
    {
        if (_applying)
        {
            return;
        }

        _masterHold.Touch();
        var v = Math.Clamp(value / 100.0, 0, 1);
        Runner.Post(e => e.SetMasterVolume(v));
    }

    partial void OnSelectedTabChanged(AppTab value)
    {
        switch (value)
        {
            case AppTab.Speakers:
                SpeakersPage.Reload();
                break;
            case AppTab.Phones:
                PhonesPage.Refresh();
                break;
            case AppTab.Settings:
                SettingsPage.Developer.Refresh();
                break;
        }
    }

    private async Task ChangeModeAsync(SourceMode mode)
    {
        _pendingMode = mode;
        Refresh();
        try
        {
            await Runner.RunAsync(e => e.SetMode(mode));
        }
        finally
        {
            _pendingMode = null;
            Refresh();
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _services.Post(() =>
    {
        SpeakersPage.Reload();
        PhonesPage.Refresh();
        SettingsPage.Developer.Refresh();
    });

    private void Apply(EngineStatus st)
    {
        var engineRunning = st.State != EngineState.Stopped;
        var running = _pendingRun ?? engineRunning;
        var calibrating = st.State == EngineState.Calibrating;
        var empty = st.Speakers.Any(s => s.EndpointId is null);

        SpeakerA.Apply(st.Speakers[0], st.State, st.AudioFlowing);
        SpeakerB.Apply(st.Speakers[1], st.State, st.AudioFlowing);

        Headline = st.Headline;
        Detail = st.Detail;
        IsEmpty = empty && st.State == EngineState.Stopped;
        DetailIsAlert = engineRunning && !calibrating && st.Speakers.Any(s => s.Problem is not null);

        // Optimistic: the rings move the moment the button is pressed, before the engine has finished opening devices.
        if (running == engineRunning)
        {
            RingA = SpeakerA.Ring;
            RingB = SpeakerB.Ring;
        }
        else if (running)
        {
            RingA = RingB = RingVisual.Connecting;
            Headline = "Looking for your speakers";
            Detail = "Connecting…";
        }
        else
        {
            RingA = st.Speakers[0].Available ? RingVisual.Idle : RingVisual.Off;
            RingB = st.Speakers[1].Available ? RingVisual.Idle : RingVisual.Off;
        }

        IsRunning = running;
        IsCalibrating = calibrating;
        IsMerged = running;
        ActionCaption = calibrating ? "Calibrating…" : running ? "Stop" : "Start";
        CanToggleRun = !calibrating && (running || !empty);

        var mode = _pendingMode ?? st.Mode;
        IsPhoneMode = mode == SourceMode.Phone;
        IsLaptopMode = mode == SourceMode.Laptop;

        if (!_masterHold.Active)
        {
            MasterVolume = Math.Round(st.MasterVolume * 100);
        }

        if (st.Error is null)
        {
            _dismissedEngineError = null;
        }

        ErrorText = _localError ?? (st.Error == _dismissedEngineError ? null : st.Error);
        WarningText = st.Warning;

        Tray = running && (RingA == RingVisual.Lost || RingB == RingVisual.Lost) ? TrayVisual.Alert
            : RingA == RingVisual.Playing || RingB == RingVisual.Playing ? TrayVisual.Playing
            : TrayVisual.Stopped;
        TrayText = Truncate(Headline, TooltipMax);
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
