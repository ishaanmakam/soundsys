using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>The real hardware backend: WASAPI + MMDevice + AudioPlaybackConnection.</summary>
public sealed class WindowsAudioBackend : IAudioBackend
{
    private readonly EndpointCatalog _catalog;
    private readonly AudioPlaybackConnectionLink _phoneLink;
    private bool _disposed;

    public WindowsAudioBackend()
    {
        // Undo an endpoint mute left behind if a previous run crashed while suppressing playback.
        PlaybackSuppressor.RestoreAfterCrash();
        _catalog = new EndpointCatalog();
        _catalog.DevicesChanged += OnDevicesChanged;
        _catalog.StartWatching();
        _phoneLink = new AudioPlaybackConnectionLink(_catalog);
    }

    public bool IsSimulated => false;

    public IClock Clock => QpcClock.Instance;

    public IPhoneLink PhoneLink => _phoneLink;

    public EndpointCatalog Catalog => _catalog;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioEndpointInfo> GetSpeakers() => _catalog.GetSpeakers();

    public IReadOnlyList<AudioEndpointInfo> GetMicrophones() => _catalog.GetMicrophones();

    public AudioEndpointInfo? GetDefaultSpeaker() => _catalog.GetDefault(render: true);

    public IOutputDevice CreateOutput(string endpointId)
    {
        var raw = _catalog.Find(endpointId) ?? throw new AudioDeviceException("That speaker isn't paired with this laptop anymore. Pair it again in Bluetooth settings.");
        var name = EndpointClassifier.DisplayName(raw, EndpointClassifier.IsBluetooth(raw));
        if (raw.MmState != EndpointClassifier.MmStateActive)
            throw new AudioDeviceException($"{name} is off or out of range.");
        return new WasapiOutputDevice(endpointId, name);
    }

    public IAudioSource CreateMicrophone(string? endpointId)
    {
        var name = "The microphone";
        if (endpointId is not null && _catalog.Find(endpointId) is { } raw)
            name = EndpointClassifier.DisplayName(raw, EndpointClassifier.IsBluetooth(raw));
        return new WasapiCaptureSource(endpointId, name, CaptureMode.Endpoint, requestRawMode: true);
    }

    public IAudioSource CreateLaptopSource(LaptopCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IAudioSource source;
        SessionMatcher? matcher = null;
        var app = options.PreferAppCapture
            ? options.AppProcessNames.FirstOrDefault(n => ProcessLoopbackSource.FindProcessId(n) is not null)
            : null;
        if (app is not null)
        {
            // Falls back to whole-laptop loopback if process loopback can't start (older Windows, app exited).
            source = new FallbackSource(new ProcessLoopbackSource(app, FriendlyAppName(app)), () => new WasapiCaptureSource(null, "This laptop", CaptureMode.Loopback));
            matcher = new SessionMatcher([app], []);
        }
        else
        {
            source = new WasapiCaptureSource(null, "This laptop", CaptureMode.Loopback);
            matcher = new SessionMatcher(options.AppProcessNames, []);
        }

        return options.Suppression == PlaybackSuppression.None
            ? source
            : new SuppressedSource(source, PlaybackSuppressor.Create(options.Suppression, null, matcher));
    }

    public int? GetBatteryPercent(string endpointId)
    {
        var raw = _catalog.Find(endpointId);
        return raw?.ContainerId is { } container ? BatteryReader.GetPercent(container) : null;
    }

    /// <summary>"AppleMusic" → "Apple Music", "Spotify" → "Spotify".</summary>
    public static string FriendlyAppName(string processName) => processName switch
    {
        _ when processName.Equals("AppleMusic", StringComparison.OrdinalIgnoreCase) => "Apple Music",
        _ => processName,
    };

    private void OnDevicesChanged(object? sender, EventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _catalog.DevicesChanged -= OnDevicesChanged;
        _phoneLink.Dispose();
        _catalog.Dispose();
    }

    /// <summary>Tries the primary source; if it can't start, uses (and from then on keeps) the fallback.</summary>
    private sealed class FallbackSource(IAudioSource primary, Func<IAudioSource> fallbackFactory) : IAudioSource
    {
        private IAudioSource _current = primary;
        private bool _switched;

        public string Id => _current.Id;

        public string DisplayName => _current.DisplayName;

        public event EventHandler<DeviceFaultEventArgs>? Faulted;

        public void Start(ISourceSink sink)
        {
            _current.Faulted -= Forward;
            _current.Faulted += Forward;
            try
            {
                _current.Start(sink);
            }
            catch (AudioDeviceException) when (!_switched)
            {
                _current.Faulted -= Forward;
                _current.Dispose();
                _current = fallbackFactory();
                _switched = true;
                _current.Faulted += Forward;
                _current.Start(sink);
            }
        }

        public void Stop() => _current.Stop();

        public void Dispose()
        {
            _current.Faulted -= Forward;
            _current.Dispose();
        }

        private void Forward(object? sender, DeviceFaultEventArgs e) => Faulted?.Invoke(this, e);
    }
}
