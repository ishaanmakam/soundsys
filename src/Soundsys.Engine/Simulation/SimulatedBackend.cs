using Soundsys.Engine.Abstractions;

namespace Soundsys.Engine.Simulation;

/// <summary>A paired phone in the simulated world.</summary>
public sealed record SimPhone(string Id, string Name, double Ppm);

/// <summary>Who and what exists in the simulated world.</summary>
public sealed class SimWorld
{
    public List<(SimSpeakerOptions Options, bool Available, int? Battery)> Speakers { get; } =
    [
        (new SimSpeakerOptions { Id = "sim:jbl-flip-6", Name = "JBL Flip 6", Ppm = 35, AcousticLatencyMs = 182, GainAtMic = 0.6, Seed = 21 }, true, 80),
        (new SimSpeakerOptions { Id = "sim:jbl-charge-5", Name = "JBL Charge 5", Ppm = -28, AcousticLatencyMs = 241, GainAtMic = 0.45, Seed = 22 }, true, 45),
        (new SimSpeakerOptions { Id = "sim:surface-speakers", Name = "Surface Laptop Speakers", IsBluetooth = false, AcousticLatencyMs = 12, GainAtMic = 0.9, Seed = 23 }, true, null),
        (new SimSpeakerOptions { Id = "sim:jbl-go-3", Name = "JBL Go 3", Ppm = 10, AcousticLatencyMs = 210, Seed = 24 }, false, null),
    ];

    public List<SimPhone> Phones { get; } =
    [
        new("sim:phone-iphone", "Ishaan's iPhone", 18),
        new("sim:phone-pixel", "Pixel 8", -22),
    ];

    public string DefaultSpeakerId { get; set; } = "sim:surface-speakers";
}

/// <summary>
/// Simulated hardware: speakers (latency, drift, disconnects), phones (connect/stream/stop), the laptop's own audio, and a room
/// with a mic. Drives everything from a <see cref="SimScheduler"/> (virtual time in tests, real time in the app's dev mode).
/// Never touches real audio devices.
/// </summary>
public sealed class SimulatedBackend : IAudioBackend
{
    private readonly SimWorld _world;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, bool> _available = [];
    private readonly Dictionary<string, SimulatedOutputDevice> _running = [];
    private readonly List<ISimPlaybackObserver> _observers = [];

    public SimulatedBackend(SimScheduler scheduler, SimWorld? world = null)
    {
        Scheduler = scheduler;
        _world = world ?? new SimWorld();
        foreach (var s in _world.Speakers)
        {
            _available[s.Options.Id] = s.Available;
        }

        Room = new SimulatedRoom(scheduler);
        _observers.Add(Room);
        Phones = new SimulatedPhoneLink(scheduler, _world.Phones);
    }

    public SimScheduler Scheduler { get; }

    public SimulatedRoom Room { get; }

    public SimulatedPhoneLink Phones { get; }

    public bool IsSimulated => true;

    public IClock Clock => Scheduler.Clock;

    public IPhoneLink PhoneLink => Phones;

    /// <summary>Is the laptop "playing" something (Laptop mode source gate)?</summary>
    public bool LaptopPlaying { get; set; } = true;

    public event EventHandler? DevicesChanged;

    /// <summary>Raised when the backend creates an output (tests attach probes).</summary>
    public event EventHandler<SimulatedOutputDevice>? OutputCreated;

    public void AddObserver(ISimPlaybackObserver observer)
    {
        lock (_lock)
        {
            _observers.Add(observer);
        }
    }

    public IReadOnlyList<AudioEndpointInfo> GetSpeakers()
    {
        lock (_lock)
        {
            return _world.Speakers
                .Select(s => new AudioEndpointInfo(s.Options.Id, s.Options.Name, s.Options.IsBluetooth, _available[s.Options.Id] ? EndpointState.Active : EndpointState.Unavailable, s.Options.ContainerId ?? s.Options.Id))
                .ToList();
        }
    }

    public IReadOnlyList<AudioEndpointInfo> GetMicrophones() =>
        [new AudioEndpointInfo("sim-mic", "Surface Microphone Array", false, EndpointState.Active)];

    public AudioEndpointInfo? GetDefaultSpeaker() => GetSpeakers().FirstOrDefault(s => s.Id == _world.DefaultSpeakerId);

    public IOutputDevice CreateOutput(string endpointId)
    {
        lock (_lock)
        {
            var entry = _world.Speakers.FirstOrDefault(s => s.Options.Id == endpointId);
            if (entry.Options is null)
            {
                throw new AudioDeviceException("That speaker isn't paired with this laptop anymore.");
            }

            if (!_available[endpointId])
            {
                throw new AudioDeviceException($"{entry.Options.Name} is off or out of range.");
            }

            var device = new SimulatedOutputDevice(Scheduler, entry.Options);
            foreach (var o in _observers)
            {
                device.AddObserver(o);
            }

            _running[endpointId] = device;
            OutputCreated?.Invoke(this, device);
            return device;
        }
    }

    public IAudioSource CreateMicrophone(string? endpointId) => Room.CreateMicrophone();

    public IAudioSource CreateLaptopSource(LaptopCaptureOptions options) =>
        new SimulatedSource(Scheduler, new SimSourceOptions
        {
            Id = "sim:laptop",
            Name = "This laptop",
            Ppm = 0,
            Gate = () => LaptopPlaying,
            Seed = 99,
        });

    public int? GetBatteryPercent(string endpointId) =>
        _world.Speakers.FirstOrDefault(s => s.Options.Id == endpointId).Battery;

    /// <summary>Switches a simulated speaker on/off. Off = it drops out mid-stream, exactly like a real speaker powering down.</summary>
    public void SetSpeakerAvailable(string endpointId, bool available)
    {
        SimulatedOutputDevice? toDrop = null;
        lock (_lock)
        {
            _available[endpointId] = available;
            if (!available && _running.Remove(endpointId, out var device))
            {
                toDrop = device;
            }
        }

        toDrop?.SimulateDisconnect();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Simulates Windows giving a speaker a new endpoint ID (driver stack change / re-pair); the physical device is the same.</summary>
    public void ReplaceSpeakerEndpoint(string oldId, string newId)
    {
        lock (_lock)
        {
            var i = _world.Speakers.FindIndex(s => s.Options.Id == oldId);
            var e = _world.Speakers[i];
            _world.Speakers[i] = (e.Options with { Id = newId, ContainerId = e.Options.ContainerId ?? oldId }, e.Available, e.Battery);
            _available[newId] = _available[oldId];
            _available.Remove(oldId);
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsSpeakerAvailable(string endpointId)
    {
        lock (_lock)
        {
            return _available.TryGetValue(endpointId, out var a) && a;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Simulated AudioPlaybackConnection: phones connect, stream, pause and leave on command.</summary>
public sealed class SimulatedPhoneLink(SimScheduler scheduler, IReadOnlyList<SimPhone> phones) : IPhoneLink
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PhoneLinkState> _state = phones.ToDictionary(p => p.Id, _ => PhoneLinkState.Waiting);

    public event EventHandler<PhoneActivityEventArgs>? ActivityChanged;

    public IReadOnlyList<PhoneInfo> GetPairedPhones() => phones.Select(p => new PhoneInfo(p.Id, p.Name)).ToList();

    public void SetEnabledPhones(IReadOnlyCollection<string> phoneIds)
    {
        var changes = new List<(string, PhoneLinkState)>();
        lock (_lock)
        {
            foreach (var p in phones)
            {
                var enabled = phoneIds.Contains(p.Id);
                var now = _state[p.Id];
                var next = enabled ? (now == PhoneLinkState.Disabled ? PhoneLinkState.Waiting : now) : PhoneLinkState.Disabled;
                if (next != now)
                {
                    _state[p.Id] = next;
                    changes.Add((p.Id, next));
                }
            }
        }

        foreach (var (id, s) in changes)
        {
            ActivityChanged?.Invoke(this, new PhoneActivityEventArgs(id, s));
        }
    }

    public IReadOnlyList<PhoneStatus> GetStatus()
    {
        lock (_lock)
        {
            return phones.Select(p => new PhoneStatus(p.Id, p.Name, _state[p.Id])).ToList();
        }
    }

    public void Release(string phoneId) => Set(phoneId, PhoneLinkState.Waiting, onlyIfLive: true);

    public IAudioSource CreateSource(string phoneId, PlaybackSuppression suppression)
    {
        var phone = phones.First(p => p.Id == phoneId);
        return new SimulatedSource(scheduler, new SimSourceOptions
        {
            Id = phone.Id,
            Name = phone.Name,
            Ppm = phone.Ppm,
            JitterMs = 4, // Bluetooth inbound packets are bursty
            Gate = () => State(phoneId) == PhoneLinkState.Streaming,
            Seed = phone.Id.Length,
        });
    }

    /// <summary>The phone picked the laptop as its speaker (connected, not yet playing).</summary>
    public void SimulateConnect(string phoneId) => Set(phoneId, PhoneLinkState.Connected, onlyIfLive: false);

    /// <summary>Press play on the phone (connects first if needed).</summary>
    public void SimulatePlay(string phoneId) => Set(phoneId, PhoneLinkState.Streaming, onlyIfLive: false);

    /// <summary>Press pause on the phone.</summary>
    public void SimulatePause(string phoneId) => Set(phoneId, PhoneLinkState.Connected, onlyIfLive: true);

    /// <summary>The phone walks away / turns Bluetooth off.</summary>
    public void SimulateLeave(string phoneId) => Set(phoneId, PhoneLinkState.Waiting, onlyIfLive: true);

    public PhoneLinkState State(string phoneId)
    {
        lock (_lock)
        {
            return _state.TryGetValue(phoneId, out var s) ? s : PhoneLinkState.Disabled;
        }
    }

    public void Dispose()
    {
    }

    private void Set(string phoneId, PhoneLinkState state, bool onlyIfLive)
    {
        lock (_lock)
        {
            var now = _state[phoneId];
            if (now == PhoneLinkState.Disabled || now == state)
            {
                return;
            }

            if (onlyIfLive && now is not (PhoneLinkState.Connected or PhoneLinkState.Streaming))
            {
                return;
            }

            _state[phoneId] = state;
        }

        ActivityChanged?.Invoke(this, new PhoneActivityEventArgs(phoneId, state));
    }
}
