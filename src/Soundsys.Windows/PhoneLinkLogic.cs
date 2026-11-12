using System.Text.RegularExpressions;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>Pure helpers for the phone link (unit-tested).</summary>
public static partial class PhoneLinkLogic
{
    /// <summary>
    /// Retry delays for AudioPlaybackConnection.OpenAsync: 0.5 s, 1 s, 2 s, 4 s… (capped at 4 s) until the total reaches
    /// <paramref name="budget"/>. Each OpenAsync itself can take several seconds to page an absent phone.
    /// </summary>
    public static IReadOnlyList<TimeSpan> OpenRetryDelays(TimeSpan budget)
    {
        var delays = new List<TimeSpan>();
        var total = TimeSpan.Zero;
        var next = TimeSpan.FromMilliseconds(500);
        while (total + next <= budget)
        {
            delays.Add(next);
            total += next;
            next = TimeSpan.FromTicks(Math.Min(next.Ticks * 2, TimeSpan.FromSeconds(4).Ticks));
        }

        return delays;
    }

    /// <summary>
    /// Finds a capture endpoint that carries this phone's audio: same device container first, then a name match.
    /// Returns null when Windows exposes none (then soundsys falls back to loopback of the default output).
    /// </summary>
    public static AudioEndpointInfo? FindPhoneCaptureEndpoint(IEnumerable<AudioEndpointInfo> captureEndpoints, string? phoneContainerId, string phoneName)
    {
        var active = captureEndpoints.Where(e => e.State == EndpointState.Active).ToList();
        if (Guid.TryParse(phoneContainerId, out var container) && container != EndpointClassifier.LocalMachineContainer)
        {
            var byContainer = active.FirstOrDefault(e => Guid.TryParse(e.ContainerId, out var c) && c == container);
            if (byContainer is not null)
                return byContainer;
        }

        if (string.IsNullOrWhiteSpace(phoneName))
            return null;
        return active.FirstOrDefault(e => e.Name.Contains(phoneName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extracts the 12-hex-digit Bluetooth address from a device or interface ID, if present.</summary>
    public static string? TryGetBluetoothAddress(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;
        var m = AddressPattern().Match(deviceId);
        return m.Success ? m.Groups["a"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"(?:Dev_|BluetoothDevice_|&)(?<a>[0-9A-Fa-f]{12})(?:_C[0-9A-Fa-f]{8}|#|\\|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
