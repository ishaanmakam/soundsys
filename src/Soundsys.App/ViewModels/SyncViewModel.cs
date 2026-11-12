using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundsys.Engine;

namespace Soundsys.App.ViewModels;

/// <summary>The Sync tab: auto-calibrate, the manual offset slider and the click track.</summary>
public sealed partial class SyncViewModel(EngineRunner runner, MainViewModel main) : ObservableObject
{
    public const double ManualRangeMs = 500;

    private readonly EditHold _manualHold = new();
    private readonly EditHold _clickHold = new();
    private bool _applying;
    private bool _calibrateRunning;

    [ObservableProperty]
    public partial bool IsCalibrating { get; set; }

    /// <summary>Live step while calibrating ("Playing a test sweep on Desk…").</summary>
    [ObservableProperty]
    public partial string? StepText { get; set; }

    /// <summary>The last result in plain words.</summary>
    [ObservableProperty]
    public partial string? ResultText { get; set; }

    /// <summary>"Desk 232 ms · Bed 291 ms → Desk waits 59 ms".</summary>
    [ObservableProperty]
    public partial string? NumbersText { get; set; }

    [ObservableProperty]
    public partial bool ResultIsProblem { get; set; }

    [ObservableProperty]
    public partial bool CanCalibrate { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>The engine is stopped (the click track needs Start first).</summary>
    [ObservableProperty]
    public partial bool IsStopped { get; set; } = true;

    /// <summary>B minus A, ms: positive = Bed later, negative = Desk later.</summary>
    [ObservableProperty]
    public partial double ManualSync { get; set; }

    [ObservableProperty]
    public partial string ManualSyncText { get; set; } = "In step";

    [ObservableProperty]
    public partial string LeftLabel { get; set; } = "← Desk later";

    [ObservableProperty]
    public partial string RightLabel { get; set; } = "Bed later →";

    [ObservableProperty]
    public partial bool ClickTrack { get; set; }

    [ObservableProperty]
    public partial bool CanClickTrack { get; set; }

    internal void Apply(EngineStatus st)
    {
        _applying = true;
        try
        {
            var calibrating = st.State == EngineState.Calibrating;
            IsCalibrating = calibrating || _calibrateRunning;
            StepText = calibrating ? st.CalibrationStep ?? "Getting ready…" : null;
            IsRunning = st.State == EngineState.Running;
            IsStopped = st.State == EngineState.Stopped;
            CanCalibrate = !IsCalibrating;
            CanClickTrack = IsRunning;
            var a = st.Speakers[0].Name;
            var b = st.Speakers[1].Name;
            LeftLabel = $"← {a} later";
            RightLabel = $"{b} later →";
            if (!_manualHold.Active)
            {
                ManualSync = Math.Round(st.SyncMs);
            }

            ManualSyncText = Describe(ManualSync, a, b);
            if (!_clickHold.Active)
            {
                ClickTrack = st.ClickTrack;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    internal static string Describe(double bMinusA, string a, string b)
    {
        var ms = Math.Round(bMinusA);
        if (ms == 0)
        {
            return "In step";
        }

        var who = ms > 0 ? b : a;
        return $"{who} waits {Math.Abs(ms).ToString("0", CultureInfo.CurrentCulture)} ms";
    }

    /// <summary>"Desk 232 ms · Bed 291 ms → Desk waits 59 ms".</summary>
    internal static string? Numbers(CalibrationReport report, string a, string b)
    {
        if (!report.Success || report.LatencyAMs is not { } la || report.LatencyBMs is not { } lb)
        {
            return null;
        }

        var c = CultureInfo.CurrentCulture;
        var wait = report.OffsetAMs - report.OffsetBMs;
        var tail = Math.Abs(wait) < 0.5
            ? "no wait needed"
            : $"{(wait > 0 ? a : b)} waits {Math.Abs(wait).ToString("0", c)} ms";
        return $"{a} {la.ToString("0", c)} ms · {b} {lb.ToString("0", c)} ms → {tail}";
    }

    [RelayCommand]
    private async Task CalibrateAsync()
    {
        if (IsCalibrating)
        {
            return;
        }

        _calibrateRunning = true;
        IsCalibrating = true;
        CanCalibrate = false;
        ResultText = null;
        NumbersText = null;
        StepText = "Getting ready…";
        try
        {
            var report = await runner.RunTaskAsync(e => e.CalibrateAsync());
            if (report is not null)
            {
                var st = main.Status;
                var a = st?.Speakers[0].Name ?? "Speaker 1";
                var b = st?.Speakers[1].Name ?? "Speaker 2";
                ResultText = report.Message;
                ResultIsProblem = !report.Success;
                NumbersText = Numbers(report, a, b);
            }
        }
        finally
        {
            _calibrateRunning = false;
            main.Refresh();
        }
    }

    partial void OnManualSyncChanged(double value)
    {
        if (_applying)
        {
            return;
        }

        _manualHold.Touch();
        var v = Math.Clamp(Math.Round(value), -ManualRangeMs, ManualRangeMs);
        var st = main.Status;
        ManualSyncText = Describe(v, st?.Speakers[0].Name ?? "Speaker 1", st?.Speakers[1].Name ?? "Speaker 2");
        runner.Post(e => e.SetManualSync(v));
    }

    partial void OnClickTrackChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        _clickHold.Touch();
        runner.Post(e =>
        {
            if (value)
            {
                e.StartClickTrack();
            }
            else
            {
                e.StopClickTrack();
            }
        });
    }
}
