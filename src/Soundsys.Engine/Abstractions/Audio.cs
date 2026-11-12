namespace Soundsys.Engine.Abstractions;

/// <summary>Sample rate + channel count of an interleaved 32-bit float stream.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    /// <summary>The format the engine's history buffer uses internally (channels only; rate follows the source).</summary>
    public const int InternalChannels = 2;

    public static AudioFormat Stereo48k => new(48000, 2);

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch";
}

/// <summary>
/// A monotonic clock in seconds. All capture and render timestamps share this time base
/// (QPC on real hardware, virtual time in simulation).
/// </summary>
public interface IClock
{
    double NowSeconds { get; }
}

/// <summary>Receives captured audio from an <see cref="IAudioSource"/>. Called on the capture thread; must not block.</summary>
public interface ISourceSink
{
    /// <summary>Called once from <see cref="IAudioSource.Start"/> before the first <see cref="Write"/>, and again if the format changes.</summary>
    void OnFormat(AudioFormat format);

    /// <param name="interleaved">Interleaved float samples in the announced format. Length is a multiple of the channel count.</param>
    /// <param name="timestampSeconds">Capture time of the first frame in <see cref="IClock"/> seconds.</param>
    /// <param name="discontinuity">True if the device reported lost data before this packet.</param>
    void Write(ReadOnlySpan<float> interleaved, double timestampSeconds, bool discontinuity);
}

/// <summary>A capture stream: phone (AudioPlaybackConnection), loopback, process loopback, mic, generator or simulation.</summary>
public interface IAudioSource : IDisposable
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Starts capturing; calls <see cref="ISourceSink.OnFormat"/> synchronously, then <see cref="ISourceSink.Write"/> from a capture thread.</summary>
    /// <exception cref="AudioDeviceException">With a plain-English message if the source can't start.</exception>
    void Start(ISourceSink sink);

    void Stop();

    /// <summary>Raised (on any thread) when the source dies, e.g. the device was removed.</summary>
    event EventHandler<DeviceFaultEventArgs>? Faulted;
}

/// <summary>Timing information supplied with every render callback.</summary>
/// <param name="TimestampSeconds">Time of this callback, <see cref="IClock"/> seconds.</param>
/// <param name="QueuedFrames">Frames (device rate) already queued in the device buffer that will play before the frames written now.</param>
public readonly record struct RenderTiming(double TimestampSeconds, int QueuedFrames);

/// <summary>Produces audio for an <see cref="IOutputDevice"/>. Implemented by the engine's OutputChannel.</summary>
public interface IRenderTarget
{
    /// <summary>Called once from <see cref="IOutputDevice.Start"/> (not on the RT thread) before the first <see cref="Render"/>.</summary>
    void Prepare(AudioFormat deviceFormat, int maxFramesPerCallback);

    /// <summary>Fill <paramref name="interleaved"/> with exactly <paramref name="frames"/> frames. Real-time thread: no allocation, no blocking.</summary>
    void Render(Span<float> interleaved, int frames, in RenderTiming timing);
}

/// <summary>A render endpoint (e.g. one JBL speaker). Pulls audio from an <see cref="IRenderTarget"/> on its own thread.</summary>
public interface IOutputDevice : IDisposable
{
    string Id { get; }
    string DisplayName { get; }

    /// <exception cref="AudioDeviceException">With a plain-English message if the device can't start.</exception>
    void Start(IRenderTarget target);

    void Stop();

    /// <summary>Raised (on any thread) when the device disconnects or fails. The device is stopped when this fires.</summary>
    event EventHandler<DeviceFaultEventArgs>? Faulted;
}

public enum DeviceFaultKind
{
    Disconnected,
    Failed,
    FormatChanged,
}

public sealed class DeviceFaultEventArgs(DeviceFaultKind kind, string message) : EventArgs
{
    public DeviceFaultKind Kind { get; } = kind;

    /// <summary>Technical detail for logs (never shown verbatim to the user).</summary>
    public string Message { get; } = message;
}

/// <summary>An audio problem with a message that is safe to show to the user as-is.</summary>
public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException() { }
    public AudioDeviceException(string userMessage) : base(userMessage) { }
    public AudioDeviceException(string userMessage, Exception inner) : base(userMessage, inner) { }
}
