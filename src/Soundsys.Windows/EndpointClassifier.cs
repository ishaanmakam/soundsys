using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>Raw facts about one MMDevice endpoint, as read from its property store (plus a container lookup).</summary>
/// <param name="Id">MMDevice ID, e.g. "{0.0.0.00000000}.{guid}".</param>
/// <param name="FriendlyName">PKEY_Device_FriendlyName, e.g. "Speakers (JBL Flip 6)".</param>
/// <param name="DeviceDescription">PKEY_Device_DeviceDesc, e.g. "Speakers" / "Headphones".</param>
/// <param name="InterfaceName">PKEY_DeviceInterface_FriendlyName: the driver/device name (BT device name on the Microsoft stack, driver name on vendor offload stacks).</param>
/// <param name="EnumeratorName">PKEY_Device_EnumeratorName, e.g. "BTHENUM", "QCASD", "HDAUDIO".</param>
/// <param name="HardwareInstanceId">Endpoint property {b3f8fa53-0004-438e-9003-51a46e139bfc},2: the owning device instance, e.g. "{1}.BTHENUM\{0000110B-...}".</param>
/// <param name="MmState">DEVICE_STATE_* bit: 1 Active, 2 Disabled, 4 NotPresent, 8 Unplugged.</param>
/// <param name="ContainerId">PKEY_Device_ContainerId.</param>
/// <param name="ContainerIsBluetooth">From the container lookup (any PnP device in the container enumerated by a Bluetooth bus); null if unknown.</param>
/// <param name="ContainerName">The container's (physical device's) name, e.g. "JBL Flip 6"; null if unknown.</param>
public sealed record RawEndpoint(
    string Id,
    string? FriendlyName,
    string? DeviceDescription,
    string? InterfaceName,
    string? EnumeratorName,
    string? HardwareInstanceId,
    int MmState,
    Guid? ContainerId,
    bool? ContainerIsBluetooth,
    string? ContainerName);

/// <summary>
/// Pure endpoint classification / naming / filtering logic (unit-tested).
///
/// Bluetooth detection, in order:
/// 1. The endpoint's enumerator or hardware instance is a Bluetooth bus (BTHENUM, BTHHFENUM, BTHLEDEVICE, BTHLE) — the Microsoft stack.
/// 2. The hardware instance / interface name mentions Bluetooth or A2DP — vendor offload stacks. Measured on this
///    Snapdragon laptop: Bluetooth endpoints come from Qualcomm's ACX driver (enumerator "QCASD", instance
///    "QCASD\A2DPSPEAKERHEADSETSPEAKER-…", interface "Qualcomm(R) Aqstic(TM) ACX A2dp Speaker Headset").
/// 3. The endpoint's device container holds a Bluetooth bus device (Windows.Devices.Enumeration lookup). This is the
///    stack-independent ground truth and also yields the real device name ("Ishaan's Beats Studio Pro").
/// </summary>
public static class EndpointClassifier
{
    /// <summary>The container of the computer itself (built-in devices).</summary>
    public static readonly Guid LocalMachineContainer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    private static readonly string[] BluetoothEnumerators = ["BTHENUM", "BTHHFENUM", "BTHLEDEVICE", "BTHLE", "BTHLEAUDIO"];

    public const int MmStateActive = 1;
    public const int MmStateDisabled = 2;
    public const int MmStateNotPresent = 4;
    public const int MmStateUnplugged = 8;

    /// <summary>True if a PnP instance ID belongs to a Bluetooth bus (e.g. "BTHENUM\Dev_F873DFD5B103\...").</summary>
    public static bool IsBluetoothInstanceId(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return false;
        var s = StripEndpointPrefix(instanceId);
        foreach (var e in BluetoothEnumerators)
        {
            if (s.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsBluetooth(RawEndpoint e)
    {
        if (e.EnumeratorName is { } en && BluetoothEnumerators.Contains(en, StringComparer.OrdinalIgnoreCase))
            return true;
        if (IsBluetoothInstanceId(e.HardwareInstanceId))
            return true;
        if (MentionsBluetooth(e.HardwareInstanceId) || MentionsBluetooth(e.InterfaceName))
            return true;
        if (e.ContainerId == LocalMachineContainer)
            return false;
        return e.ContainerIsBluetooth == true;
    }

    /// <summary>Hands-free (HFP) endpoints: 16 kHz mono call audio, never what we want for music.</summary>
    public static bool IsHandsFree(RawEndpoint e)
    {
        static bool Hf(string? s) => s is not null
            && (s.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                || s.Contains("HANDSFREE", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Hands Free", StringComparison.OrdinalIgnoreCase));
        return Hf(e.HardwareInstanceId) || Hf(e.InterfaceName) || Hf(e.EnumeratorName);
    }

    private static bool MentionsBluetooth(string? s) => s is not null
        && (s.Contains("BLUETOOTH", StringComparison.OrdinalIgnoreCase) || s.Contains("A2DP", StringComparison.OrdinalIgnoreCase));

    private static string StripEndpointPrefix(string s)
    {
        // Endpoint copies of instance IDs look like "{1}.BTHENUM\..." or "{2}.\\?\bthenum#...".
        if (s.Length > 4 && s[0] == '{')
        {
            var close = s.IndexOf("}.", StringComparison.Ordinal);
            if (close > 0)
                s = s[(close + 2)..];
        }

        return s;
    }

    public static EndpointState ToEndpointState(int mmState) => mmState switch
    {
        MmStateActive => EndpointState.Active,
        MmStateDisabled => EndpointState.Disabled,
        _ => EndpointState.Unavailable,
    };

    /// <summary>
    /// The name to show. Bluetooth endpoints use the physical device's name (container name, or the interface name on the
    /// Microsoft stack), because vendor stacks name the interface after the driver. Others use the full friendly name.
    /// </summary>
    public static string DisplayName(RawEndpoint e, bool isBluetooth)
    {
        if (isBluetooth)
        {
            if (!string.IsNullOrWhiteSpace(e.ContainerName))
                return e.ContainerName.Trim();
            if (!string.IsNullOrWhiteSpace(e.InterfaceName) && !LooksLikeDriverName(e.InterfaceName))
                return e.InterfaceName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(e.FriendlyName) && !string.Equals(e.FriendlyName, "Unknown", StringComparison.Ordinal))
            return e.FriendlyName.Trim();
        if (!string.IsNullOrWhiteSpace(e.DeviceDescription) && !string.IsNullOrWhiteSpace(e.InterfaceName))
            return $"{e.DeviceDescription.Trim()} ({e.InterfaceName.Trim()})";
        return e.DeviceDescription?.Trim() is { Length: > 0 } d ? d : "Audio device";
    }

    private static bool LooksLikeDriverName(string name)
        => name.Contains("(R)", StringComparison.Ordinal)
           || name.Contains("(TM)", StringComparison.Ordinal)
           || name.Contains("Audio Device", StringComparison.OrdinalIgnoreCase)
           || name.Contains("A2dp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Speaker list: every active endpoint, plus Bluetooth endpoints that are paired but off (Unavailable).
    /// Hands-free endpoints are dropped when the same device has a non-hands-free one; inactive Bluetooth duplicates
    /// (old driver stacks leave several per device) collapse to one per container, and none is shown if the device
    /// already has an active endpoint. Duplicate names get " (2)", " (3)"… in a stable order.
    /// </summary>
    public static IReadOnlyList<AudioEndpointInfo> SelectSpeakers(IEnumerable<RawEndpoint> endpoints)
        => Select(endpoints, includeUnavailableBluetooth: true);

    /// <summary>Microphones: active endpoints only.</summary>
    public static IReadOnlyList<AudioEndpointInfo> SelectMicrophones(IEnumerable<RawEndpoint> endpoints)
        => Select(endpoints, includeUnavailableBluetooth: false);

    private static List<AudioEndpointInfo> Select(IEnumerable<RawEndpoint> endpoints, bool includeUnavailableBluetooth)
    {
        var items = endpoints
            .Select(e => (Raw: e, Bt: IsBluetooth(e), Hf: IsHandsFree(e)))
            .Where(x => x.Raw.MmState == MmStateActive || (includeUnavailableBluetooth && x.Bt && x.Raw.MmState is MmStateUnplugged or MmStateNotPresent))
            .ToList();

        // Drop hands-free endpoints when the same container offers a proper one.
        items = items
            .Where(x => !x.Hf || x.Raw.ContainerId is not { } c || !items.Any(o => !o.Hf && o.Raw.ContainerId == c))
            .ToList();

        // Collapse inactive Bluetooth endpoints: one per container, none if the device has an active endpoint.
        var result = new List<(RawEndpoint Raw, bool Bt)>();
        foreach (var group in items.GroupBy(x => x.Raw.MmState != MmStateActive && x.Bt && x.Raw.ContainerId is not null ? x.Raw.ContainerId : null))
        {
            if (group.Key is null)
            {
                result.AddRange(group.Select(x => (x.Raw, x.Bt)));
                continue;
            }

            if (items.Any(o => o.Raw.MmState == MmStateActive && o.Raw.ContainerId == group.Key))
                continue;
            var best = group
                .OrderBy(x => x.Raw.MmState == MmStateUnplugged ? 0 : 1) // Unplugged = known and recently seen
                .ThenBy(x => IsBluetoothInstanceId(x.Raw.HardwareInstanceId) ? 1 : 0) // prefer the current (vendor) stack
                .ThenBy(x => x.Raw.Id, StringComparer.OrdinalIgnoreCase)
                .First();
            result.Add((best.Raw, best.Bt));
        }

        var ordered = result
            .OrderBy(x => x.Raw.MmState == MmStateActive ? 0 : 1)
            .ThenBy(x => x.Raw.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => new AudioEndpointInfo(
                x.Raw.Id,
                DisplayName(x.Raw, x.Bt),
                x.Bt,
                ToEndpointState(x.Raw.MmState),
                x.Raw.ContainerId?.ToString("B")))
            .ToList();
        return MakeNamesUnique(ordered);
    }

    /// <summary>Appends " (2)", " (3)"… to repeated names (e.g. two identical JBL speakers). Input order is kept.</summary>
    public static List<AudioEndpointInfo> MakeNamesUnique(IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AudioEndpointInfo>(endpoints.Count);
        foreach (var e in endpoints)
        {
            if (seen.TryGetValue(e.Name, out var n))
            {
                seen[e.Name] = n + 1;
                list.Add(e with { Name = $"{e.Name} ({n + 1})" });
            }
            else
            {
                seen[e.Name] = 1;
                list.Add(e);
            }
        }

        return list;
    }

    /// <summary>
    /// Normalizes an MMDevice ID or a WinRT/SWD device-interface ID ("\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{...}")
    /// to the MMDevice form so endpoints can be compared across APIs.
    /// </summary>
    public static string NormalizeEndpointId(string id)
    {
        const string marker = "MMDEVAPI#";
        var i = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return id;
        var rest = id[(i + marker.Length)..];
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? rest : rest[..hash];
    }
}
