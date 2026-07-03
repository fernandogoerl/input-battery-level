using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryTray.Sources;

/// <summary>
/// Reports battery for paired Bluetooth devices using the battery level that Windows
/// already tracks (the same value the Settings app shows). This works for:
/// <list type="bullet">
///   <item>Bluetooth LE devices exposing the standard GATT Battery Service (0x180F).</item>
///   <item>Classic Bluetooth devices that report battery (many headsets, mice).</item>
/// </list>
/// We read the PnP property <c>DEVPKEY_Bluetooth_Battery</c>
/// (<c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2</c>) that Windows populates on the device,
/// so we don't have to open a GATT connection ourselves.
/// </summary>
public sealed class BluetoothBatterySource : IBatterySource
{
    public string Name => "Bluetooth";

    private const string BatteryPropertyKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public IReadOnlyList<BatteryReading> Poll()
    {
        try
        {
            return PollAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return Array.Empty<BatteryReading>();
        }
    }

    private async Task<IReadOnlyList<BatteryReading>> PollAsync()
    {
        var results = new List<BatteryReading>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Query paired classic BT and BLE devices; the battery property is requested as an
        // additional property so it comes back in each device's Properties bag.
        string[] selectors =
        {
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
        };

        foreach (var selector in selectors)
        {
            DeviceInformationCollection devices;
            try
            {
                devices = await DeviceInformation.FindAllAsync(selector, new[] { BatteryPropertyKey });
            }
            catch
            {
                continue;
            }

            foreach (var di in devices)
            {
                if (!di.Properties.TryGetValue(BatteryPropertyKey, out var raw) || raw is null)
                    continue;

                int pct;
                try { pct = Convert.ToInt32(raw); }
                catch { continue; }

                if (pct is < 0 or > 100)
                    continue;

                string name = string.IsNullOrWhiteSpace(di.Name) ? "Bluetooth device" : di.Name.Trim();

                // Dedup: BLE + classic selectors can surface the same physical device.
                if (!seen.Add(name))
                    continue;

                results.Add(new BatteryReading
                {
                    Id = $"bt:{di.Id}",
                    Name = name,
                    Kind = GuessKind(name),
                    Percentage = pct,
                    IsConnected = true,
                    IsCharging = false, // Windows doesn't expose BT charge state here.
                });
            }
        }

        return results;
    }

    private static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("keyboard")) return DeviceKind.Keyboard;
        if (n.Contains("mouse") || n.Contains("mx ") || n.Contains("trackpad")) return DeviceKind.Mouse;
        if (n.Contains("controller") || n.Contains("gamepad")) return DeviceKind.XboxController;
        return DeviceKind.Unknown;
    }
}
