namespace Soundsys.App.ViewModels;

/// <summary>How one ring of the signature control looks.</summary>
public enum RingVisual
{
    /// <summary>No speaker chosen, or the speaker is off (grey).</summary>
    Off,

    /// <summary>Stopped and available (outlined, muted).</summary>
    Idle,

    /// <summary>Opening or reconnecting (outlined, breathing).</summary>
    Connecting,

    /// <summary>Connected but nothing is playing (amber outline).</summary>
    Waiting,

    /// <summary>Playing (amber).</summary>
    Playing,

    /// <summary>Off or out of range while running (dashed, alert).</summary>
    Lost,

    /// <summary>Calibration in progress (amber, breathing).</summary>
    Calibrating,
}

/// <summary>The small status dot on a speaker row.</summary>
public enum SpeakerDot
{
    Off,
    Idle,
    Connecting,
    Playing,
    Lost,
}

public enum AppTab
{
    Home,
    Speakers,
    Phones,
    Sync,
    Settings,
}

/// <summary>Tray icon colour.</summary>
public enum TrayVisual
{
    Stopped,
    Playing,
    Alert,
}
