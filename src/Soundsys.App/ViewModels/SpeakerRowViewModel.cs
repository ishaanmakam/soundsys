using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Soundsys.Engine;
using Soundsys.Engine.Sync;

namespace Soundsys.App.ViewModels;

/// <summary>One speaker row on Home: state dot, name, device, battery, volume, sync offset.</summary>
public sealed partial class SpeakerRowViewModel(SpeakerSlot slot, EngineRunner runner) : ObservableObject
{
    private readonly EditHold _volumeHold = new();
    private bool _applying;

    public SpeakerSlot Slot => slot;

    [ObservableProperty]
    public partial string Name { get; set; } = slot == SpeakerSlot.A ? "Speaker 1" : "Speaker 2";

    [ObservableProperty]
    public partial string DeviceText { get; set; } = "Not chosen";

    [ObservableProperty]
    public partial SpeakerDot Dot { get; set; }

    /// <summary>"Playing", "Off or out of range"… (screen readers, tooltips).</summary>
    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    [ObservableProperty]
    public partial string? BatteryText { get; set; }

    /// <summary>Segoe Fluent Icons battery glyph for the level.</summary>
    [ObservableProperty]
    public partial string BatteryGlyph { get; set; } = "";

    [ObservableProperty]
    public partial string? OffsetText { get; set; }

    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    /// <summary>0..100.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial RingVisual Ring { get; set; }

    internal void Apply(SpeakerStatus s, EngineState state, bool audioFlowing)
    {
        _applying = true;
        try
        {
            Name = s.Name;
            IsChosen = s.EndpointId is not null;
            // The device name only adds something when the speaker has its own name ("Desk" · JBL Flip 6).
            var device = string.Equals(s.DeviceName, s.Name, StringComparison.OrdinalIgnoreCase) ? "" : s.DeviceName;
            DeviceText = !IsChosen ? "Not chosen"
                : s.Available || state != EngineState.Stopped ? device
                : device.Length == 0 ? "off" : $"{device} · off";
            (Dot, StateText) = DotFor(s, state);
            Ring = RingFor(s, state, audioFlowing);
            BatteryText = s.BatteryPercent is { } b ? $"{b}%" : null;
            BatteryGlyph = s.BatteryPercent is { } level ? BatteryGlyphFor(level) : "";
            OffsetText = s.OffsetMs >= 0.5 ? $"+{s.OffsetMs.ToString("0", CultureInfo.CurrentCulture)} ms" : null;
            if (!_volumeHold.Active)
            {
                Volume = Math.Round(s.Volume * 100);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>The ring for one speaker (see the state table in docs/UI_DESIGN.md).</summary>
    internal static RingVisual RingFor(SpeakerStatus s, EngineState state, bool audioFlowing) => state switch
    {
        _ when s.EndpointId is null => RingVisual.Off,
        EngineState.Stopped => s.Available ? RingVisual.Idle : RingVisual.Off,
        EngineState.Calibrating => RingVisual.Calibrating,
        _ => s.Link switch
        {
            SpeakerLinkState.Connected => audioFlowing ? RingVisual.Playing : RingVisual.Waiting,
            SpeakerLinkState.Lost when s.Problem is not null => RingVisual.Lost,
            _ => RingVisual.Connecting,
        },
    };

    internal static string BatteryGlyphFor(int percent)
    {
        // Segoe Fluent Icons / MDL2: Battery0..Battery9 = E850..E859, Battery10 = E83F.
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }

    partial void OnVolumeChanged(double value)
    {
        if (_applying)
        {
            return;
        }

        _volumeHold.Touch();
        var v = Math.Clamp(value / 100.0, 0, 1);
        runner.Post(e => e.SetSpeakerVolume(slot, v));
    }

    private static (SpeakerDot, string) DotFor(SpeakerStatus s, EngineState state)
    {
        if (s.EndpointId is null)
        {
            return (SpeakerDot.Off, "Not chosen");
        }

        if (state == EngineState.Stopped)
        {
            return s.Available ? (SpeakerDot.Idle, "On") : (SpeakerDot.Off, "Off");
        }

        return s.Link switch
        {
            SpeakerLinkState.Connected => (SpeakerDot.Playing, s.Playing ? "Playing" : "Connected"),
            SpeakerLinkState.Lost when s.Problem is not null => (SpeakerDot.Lost, "Off or out of range"),
            _ => (SpeakerDot.Connecting, "Connecting"),
        };
    }
}
