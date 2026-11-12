using Windows.Devices.Enumeration;

namespace Soundsys.Windows;

/// <summary>
/// Best-effort Bluetooth battery level: DEVPKEY_Bluetooth_Battery ("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2") on the
/// PnP devices of the endpoint's container (Windows fills it from HFP/AVRCP battery reports while the device is connected).
/// </summary>
public static class BatteryReader
{
    public const string BatteryPropertyKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public static int? GetPercent(Guid containerId)
    {
        if (containerId == EndpointClassifier.LocalMachineContainer || containerId == Guid.Empty)
            return null;
        try
        {
            return Task.Run(async () =>
            {
                var aqs = $"System.Devices.ContainerId:=\"{containerId:B}\"";
                var devices = await DeviceInformation.FindAllAsync(aqs, [BatteryPropertyKey], DeviceInformationKind.Device);
                foreach (var d in devices)
                {
                    if (d.Properties.TryGetValue(BatteryPropertyKey, out var value) && ToPercent(value) is { } percent)
                        return percent;
                }

                return (int?)null;
            }).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Converts the property value (a byte 0–100) to a percentage; null if absent or out of range.</summary>
    public static int? ToPercent(object? value)
    {
        int? v = value switch
        {
            byte b => b,
            sbyte sb => sb,
            ushort us => us,
            short s => s,
            uint ui => (int)Math.Min(ui, int.MaxValue),
            int i => i,
            _ => null,
        };
        return v is >= 0 and <= 100 ? v : null;
    }
}
