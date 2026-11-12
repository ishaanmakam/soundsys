using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Soundsys.Engine.Abstractions;
using Windows.Devices.Enumeration;

namespace Soundsys.Windows;

/// <summary>
/// Enumerates render/capture endpoints (NAudio MMDeviceEnumerator), classifies them (see <see cref="EndpointClassifier"/>),
/// and watches for changes through IMMNotificationClient, debounced by 200 ms and raised on the thread pool.
/// </summary>
public sealed class EndpointCatalog : IDisposable
{
    private static readonly PropertyKey PkeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey PkeyDeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey PkeyDeviceEnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey PkeyDeviceInterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey PkeyEndpointHardwareInstance = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
    private static readonly PropertyKey PkeyDeviceContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    private const DeviceState ListedStates = DeviceState.Active | DeviceState.Unplugged | DeviceState.NotPresent;

    private readonly ConcurrentDictionary<Guid, (ContainerFacts? Facts, long Expires)> _containers = new();
    private static readonly long FactsLifetime = System.Diagnostics.Stopwatch.Frequency * 300;
    private static readonly long FailedLookupLifetime = System.Diagnostics.Stopwatch.Frequency * 30;
    private readonly object _watchGate = new();
    private MMDeviceEnumerator? _watchEnumerator;
    private NotificationClient? _notificationClient;
    private Timer? _debounce;
    private bool _disposed;

    private sealed record ContainerFacts(bool IsBluetooth, string? Name, IReadOnlyList<string> InstanceIds);

    /// <summary>Raised ~200 ms after the last endpoint change (added/removed/state/default), on the thread pool.</summary>
    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioEndpointInfo> GetSpeakers() => EndpointClassifier.SelectSpeakers(ReadRaw(DataFlow.Render));

    public IReadOnlyList<AudioEndpointInfo> GetMicrophones() => EndpointClassifier.SelectMicrophones(ReadRaw(DataFlow.Capture));

    /// <summary>The default render endpoint for the Console role (what apps play to), if any.</summary>
    public AudioEndpointInfo? GetDefault(bool render = true)
    {
        var flow = render ? DataFlow.Render : DataFlow.Capture;
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Console))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
        var raw = Read(device);
        var bt = EndpointClassifier.IsBluetooth(raw);
        return new AudioEndpointInfo(raw.Id, EndpointClassifier.DisplayName(raw, bt), bt, EndpointClassifier.ToEndpointState(raw.MmState), raw.ContainerId?.ToString("B"));
    }

    /// <summary>All endpoints with their raw properties, every state (for diagnostics).</summary>
    public IReadOnlyList<RawEndpoint> ReadRaw(DataFlow flow, DeviceState states = ListedStates)
    {
        var list = new List<RawEndpoint>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, states))
        {
            using (device)
            {
                try
                {
                    list.Add(Read(device));
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Endpoint vanished mid-enumeration.
                }
            }
        }

        return list;
    }

    /// <summary>Looks up one endpoint by ID (any state); null if it doesn't exist.</summary>
    public RawEndpoint? Find(string endpointId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(endpointId);
            return Read(device);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private RawEndpoint Read(MMDevice device)
    {
        var props = device.Properties;
        var container = ReadGuid(props, PkeyDeviceContainerId);
        ContainerFacts? facts = container is { } c && c != EndpointClassifier.LocalMachineContainer ? GetContainerFacts(c) : null;
        return new RawEndpoint(
            device.ID,
            ReadString(props, PkeyDeviceFriendlyName),
            ReadString(props, PkeyDeviceDesc),
            ReadString(props, PkeyDeviceInterfaceFriendlyName),
            ReadString(props, PkeyDeviceEnumeratorName),
            ReadString(props, PkeyEndpointHardwareInstance),
            (int)device.State,
            container,
            facts?.IsBluetooth,
            facts?.Name);
    }

    internal static string? ReadString(PropertyStore props, PropertyKey key)
    {
        try
        {
            return props.Contains(key) ? props[key].Value as string : null;
        }
        catch (Exception ex) when (ex is NotImplementedException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return null;
        }
    }

    internal static Guid? ReadGuid(PropertyStore props, PropertyKey key)
    {
        try
        {
            if (!props.Contains(key))
                return null;
            return props[key].Value switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var g) => g,
                byte[] { Length: >= 16 } b => new Guid(b.AsSpan(b.Length - 16, 16)),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is NotImplementedException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Container lookup through Windows.Devices.Enumeration: Bluetooth if any PnP device in the container is enumerated by
    /// a Bluetooth bus; the container name is the physical device's name. Cached per container for the catalog lifetime.
    /// </summary>
    private ContainerFacts? GetContainerFacts(Guid container)
    {
        // Cached with an expiry: facts can change while pairing (the Bluetooth node appears after the endpoint) or on
        // rename; failed/timed-out lookups are cached briefly so a stalled query can't block every enumeration.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_containers.TryGetValue(container, out var cached) && cached.Expires > now)
            return cached.Facts;
        try
        {
            var facts = Task.Run(async () =>
            {
                var aqs = $"System.Devices.ContainerId:=\"{container:B}\"";
                var devices = await DeviceInformation.FindAllAsync(aqs, null, DeviceInformationKind.Device);
                var ids = devices.Select(d => d.Id).ToList();
                string? name = null;
                try
                {
                    var c = await DeviceInformation.CreateFromIdAsync(container.ToString("B"), null, DeviceInformationKind.DeviceContainer);
                    name = c.Name;
                }
                catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or FileNotFoundException)
                {
                    // Container without a node (rare): keep name null.
                }

                return new ContainerFacts(ids.Any(EndpointClassifier.IsBluetoothInstanceId), name, ids);
            }).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _containers[container] = (facts, now + FactsLifetime);
            return facts;
        }
        catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            _containers[container] = (null, now + FailedLookupLifetime);
            return null;
        }
    }

    /// <summary>PnP instance IDs in an endpoint's container (cached), for battery and diagnostics.</summary>
    public IReadOnlyList<string> GetContainerInstanceIds(Guid container)
        => GetContainerFacts(container)?.InstanceIds ?? [];

    /// <summary>Starts the IMMNotificationClient watcher (idempotent).</summary>
    public void StartWatching()
    {
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watchEnumerator is not null)
                return;
            _debounce = new Timer(static s => ((EndpointCatalog)s!).RaiseDevicesChanged(), this, Timeout.Infinite, Timeout.Infinite);
            _notificationClient = new NotificationClient(this);
            _watchEnumerator = new MMDeviceEnumerator();
            _watchEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
    }

    private void OnEndpointNotification()
    {
        // Called on an MMDevice COM thread: never call back into the enumerator here, just (re)arm the debounce timer.
        // Container facts (Bluetooth or not, device name) don't change for a container ID, so the cache is kept.
        lock (_watchGate)
        {
            _debounce?.Change(200, Timeout.Infinite);
        }
    }

    private void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        NotificationClient? client;
        Timer? debounce;
        lock (_watchGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            enumerator = _watchEnumerator;
            client = _notificationClient;
            debounce = _debounce;
            _watchEnumerator = null;
            _debounce = null;
        }

        // Unregister outside the lock: Unregister waits for in-flight callbacks, and callbacks take _watchGate.
        if (enumerator is not null && client is not null)
        {
            try
            {
                enumerator.UnregisterEndpointNotificationCallback(client);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }

        enumerator?.Dispose();
        debounce?.Dispose();
    }

    /// <summary>IMMNotificationClient sink. Public because built-in COM callable wrappers are most reliable for public types.</summary>
    public sealed class NotificationClient : IMMNotificationClient
    {
        private readonly WeakReference<EndpointCatalog> _owner;

        internal NotificationClient(EndpointCatalog owner) => _owner = new WeakReference<EndpointCatalog>(owner);

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Notify();

        public void OnDeviceAdded(string pwstrDeviceId) => Notify();

        public void OnDeviceRemoved(string deviceId) => Notify();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Notify();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Ignored: drivers emit these constantly (levels, jack info) and they don't change the device list.
        }

        private void Notify()
        {
            if (_owner.TryGetTarget(out var owner))
                owner.OnEndpointNotification();
        }
    }
}
