namespace Soundsys.Engine.Abstractions;

public enum EndpointState
{
    /// <summary>Connected and usable.</summary>
    Active,

    /// <summary>Known/paired but currently off, out of range or unplugged.</summary>
    Unavailable,

    Disabled,
}

/// <summary>A render or capture endpoint.</summary>
/// <param name="Id">Stable endpoint ID (MMDevice ID on Windows).</param>
/// <param name="Name">Friendly name, e.g. "JBL Flip 6".</param>
/// <param name="IsBluetooth">True for Bluetooth endpoints.</param>
/// <param name="ContainerId">Device container (groups endpoints of the same physical device), if known.</param>
public sealed record AudioEndpointInfo(string Id, string Name, bool IsBluetooth, EndpointState State, string? ContainerId = null);

/// <summary>A paired phone that can stream to the laptop via AudioPlaybackConnection.</summary>
public sealed record PhoneInfo(string Id, string Name);

public enum PhoneLinkState
{
    /// <summary>The user has disabled this phone as a source.</summary>
    Disabled,

    /// <summary>The laptop is ready to receive audio but the phone isn't connected.</summary>
    Waiting,

    /// <summary>The phone is connected to the laptop as its Bluetooth speaker.</summary>
    Connected,

    /// <summary>Audio is arriving from the phone.</summary>
    Streaming,

    Error,
}

public sealed record PhoneStatus(string Id, string Name, PhoneLinkState State, string? Detail = null);

public sealed class PhoneActivityEventArgs(string phoneId, PhoneLinkState state) : EventArgs
{
    public string PhoneId { get; } = phoneId;
    public PhoneLinkState State { get; } = state;
}

/// <summary>How to stop Windows from also playing the captured audio on the default output (double audio).</summary>
public enum PlaybackSuppression
{
    /// <summary>Do nothing.</summary>
    None,

    /// <summary>Mute the default render endpoint while loopback-capturing it (only valid if this driver's loopback is pre-mute). Restored on stop.</summary>
    MuteEndpointDuringLoopback,

    /// <summary>Mute the audio session(s) of the source process/stream (only valid if capture is unaffected by session mute). Restored on stop.</summary>
    MuteSourceSession,
}

/// <summary>Options for Laptop mode capture.</summary>
/// <param name="PreferAppCapture">Use process loopback of <paramref name="AppProcessNames"/> when one of them is running.</param>
/// <param name="AppProcessNames">Process names without extension, e.g. "Spotify", "AppleMusic".</param>
public sealed record LaptopCaptureOptions(bool PreferAppCapture, IReadOnlyList<string> AppProcessNames, PlaybackSuppression Suppression)
{
    public static LaptopCaptureOptions Default { get; } = new(true, ["Spotify", "AppleMusic"], PlaybackSuppression.None);
}

/// <summary>Bluetooth phones streaming to the laptop (Windows AudioPlaybackConnection).</summary>
public interface IPhoneLink : IDisposable
{
    IReadOnlyList<PhoneInfo> GetPairedPhones();

    /// <summary>Make the laptop available as a Bluetooth speaker for exactly these phones (others are closed).</summary>
    void SetEnabledPhones(IReadOnlyCollection<string> phoneIds);

    IReadOnlyList<PhoneStatus> GetStatus();

    /// <summary>Raised when a phone connects, starts or stops streaming (any thread).</summary>
    event EventHandler<PhoneActivityEventArgs>? ActivityChanged;

    /// <summary>A capture source carrying the given phone's audio.</summary>
    IAudioSource CreateSource(string phoneId, PlaybackSuppression suppression);
}

/// <summary>Every hardware touchpoint. Real: WindowsAudioBackend. Simulated: SimulatedBackend.</summary>
public interface IAudioBackend : IDisposable
{
    bool IsSimulated { get; }

    IClock Clock { get; }

    /// <summary>Render endpoints, including paired Bluetooth speakers that are currently off (State = Unavailable).</summary>
    IReadOnlyList<AudioEndpointInfo> GetSpeakers();

    IReadOnlyList<AudioEndpointInfo> GetMicrophones();

    /// <summary>The Windows default render endpoint (what apps play to), if any.</summary>
    AudioEndpointInfo? GetDefaultSpeaker();

    /// <exception cref="AudioDeviceException">Plain-English message if the speaker isn't available.</exception>
    IOutputDevice CreateOutput(string endpointId);

    /// <summary>A microphone capture source (raw mode where supported). Null ID = default mic.</summary>
    IAudioSource CreateMicrophone(string? endpointId);

    /// <summary>Capture of what the laptop itself is playing (Laptop mode).</summary>
    IAudioSource CreateLaptopSource(LaptopCaptureOptions options);

    IPhoneLink PhoneLink { get; }

    /// <summary>Battery percentage if Windows exposes it for this endpoint's device.</summary>
    int? GetBatteryPercent(string endpointId);

    /// <summary>Endpoints were added, removed, or changed state (any thread).</summary>
    event EventHandler? DevicesChanged;
}

/// <summary>Keeps the laptop awake while soundsys is active.</summary>
public interface IPowerManager
{
    void SetKeepAwake(bool keepAwake);
}

/// <summary>"Start with Windows" registration.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
