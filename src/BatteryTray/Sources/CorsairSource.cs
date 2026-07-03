using HidSharp;

namespace BatteryTray.Sources;

/// <summary>
/// Reads battery for Corsair wireless devices (Slipstream dongle, VID 0x1B1C) using the
/// "Bragi" read-property protocol that recent Corsair wireless peripherals speak — the
/// same approach OpenRGB uses. A short command asks for property <c>0x0F</c> (battery
/// level, reported in tenths of a percent) and <c>0x10</c> (charging state).
///
/// <para><b>Unverified against hardware.</b> Corsair's protocol varies across device
/// generations; this targets the modern Bragi dongles. It is VID-guarded and fails closed:
/// if nothing answers with a plausible 0-100% value, it reports nothing.</para>
/// </summary>
public sealed class CorsairSource : IBatterySource
{
    public string Name => "Corsair";

    private const int CorsairVendorId = 0x1B1C;

    private const byte BragiReadProperty = 0x08;
    private const byte PropBatteryLevel = 0x0F; // value in 0.1% units (0-1000)
    private const byte PropBatteryStatus = 0x10; // 1 = charging

    public IReadOnlyList<BatteryReading> Poll()
    {
        var results = new List<BatteryReading>();

        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices(vendorID: CorsairVendorId); }
        catch { return results; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dev in devices)
        {
            int outLen, inLen;
            try { outLen = dev.GetMaxOutputReportLength(); inLen = dev.GetMaxInputReportLength(); }
            catch { continue; }
            if (outLen < 5 || inLen < 6) continue;

            string path;
            try { path = dev.DevicePath; } catch { continue; }
            if (!seen.Add(path)) continue;

            try
            {
                var reading = PollDevice(dev, path, outLen, inLen);
                if (reading is not null)
                    results.Add(reading);
            }
            catch
            {
                // Wrong interface / asleep / different Corsair generation — ignore.
            }
        }

        return results;
    }

    private static BatteryReading? PollDevice(HidDevice dev, string path, int outLen, int inLen)
    {
        if (!dev.TryOpen(out HidStream stream))
            return null;

        using (stream)
        {
            stream.ReadTimeout = 500;
            stream.WriteTimeout = 500;

            var levelResp = ReadProperty(stream, PropBatteryLevel, outLen, inLen);
            if (levelResp is null) return null;

            // Response payload: [.. , value_lo, value_hi] as tenths of a percent.
            int tenths = levelResp.Value.lo | (levelResp.Value.hi << 8);
            if (tenths is < 0 or > 1000) return null;
            int percent = Math.Clamp((int)Math.Round(tenths / 10.0), 0, 100);

            bool charging = false;
            var statusResp = ReadProperty(stream, PropBatteryStatus, outLen, inLen);
            if (statusResp is not null)
                charging = statusResp.Value.lo == 1;

            string name = FriendlyName(dev);
            return new BatteryReading
            {
                Id = $"corsair:{path}",
                Name = name,
                Kind = GuessKind(name),
                Percentage = percent,
                IsCharging = charging,
                IsConnected = true,
            };
        }
    }

    /// <summary>Sends a Bragi read-property command and returns the two value bytes.</summary>
    private static (byte lo, byte hi)? ReadProperty(HidStream stream, byte property, int outLen, int inLen)
    {
        var req = new byte[outLen]; // req[0] = report id 0
        req[1] = BragiReadProperty;
        req[2] = property;

        try { stream.Write(req); }
        catch { return null; }

        Thread.Sleep(40);

        var buf = new byte[inLen];
        try
        {
            int n = stream.Read(buf);
            if (n < 6) return null;
        }
        catch { return null; }

        // Echoed command/property in [1]/[2]; value follows at [4]/[5].
        if (buf[1] != BragiReadProperty || buf[2] != property)
            return null;
        return (buf[4], buf[5]);
    }

    private static string FriendlyName(HidDevice dev)
    {
        try
        {
            string n = dev.GetProductName();
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
        }
        catch { }
        return "Corsair device";
    }

    private static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("keyboard") || n.Contains("k57") || n.Contains("k63") ||
            n.Contains("k100")) return DeviceKind.Keyboard;
        if (n.Contains("headset") || n.Contains("void") || n.Contains("virtuoso") ||
            n.Contains("hs")) return DeviceKind.Headset;
        if (n.Contains("mouse") || n.Contains("dark core") || n.Contains("ironclaw") ||
            n.Contains("sabre") || n.Contains("nightsword") || n.Contains("katar") ||
            n.Contains("m75")) return DeviceKind.Mouse;
        return DeviceKind.Unknown;
    }
}
