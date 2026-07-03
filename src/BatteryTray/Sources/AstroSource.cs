using HidSharp;

namespace BatteryTray.Sources;

/// <summary>
/// Reads battery for the Astro A50 wireless headset via its USB base station
/// (Astro Gaming, VID 0x9886). The base station exposes a HID status report whose battery
/// nibble encodes the charge as a coarse 0-10 level plus a charging/docked flag — the same
/// field community A50 battery tools read.
///
/// <para><b>Unverified against hardware.</b> Implemented from public A50 notes; the A50's
/// firmware differs across revisions. VID-guarded and fail-closed: if the base station
/// isn't present or doesn't return a plausible status frame, it reports nothing.</para>
/// </summary>
public sealed class AstroSource : IBatterySource
{
    public string Name => "Astro";

    private const int AstroVendorId = 0x9886;

    // The A50 base station answers a "request device status" command with a frame whose
    // battery nibble is 0-10 (empty..full).
    private const byte CmdRequestStatus = 0x02;

    public IReadOnlyList<BatteryReading> Poll()
    {
        var results = new List<BatteryReading>();

        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices(vendorID: AstroVendorId); }
        catch { return results; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dev in devices)
        {
            int outLen, inLen;
            try { outLen = dev.GetMaxOutputReportLength(); inLen = dev.GetMaxInputReportLength(); }
            catch { continue; }
            if (outLen < 5 || inLen < 5) continue;

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
                // Wrong interface / base station off — ignore.
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

            // Ask the base station for a status frame.
            var req = new byte[outLen]; // req[0] = report id 0
            req[1] = CmdRequestStatus;
            try { stream.Write(req); }
            catch { return null; }

            Thread.Sleep(40);

            var buf = new byte[inLen];
            int n;
            try { n = stream.Read(buf); }
            catch { return null; }
            if (n < 4) return null;

            // Battery nibble: low 4 bits = level 0-10, high bit of the byte = charging/docked.
            // The status byte sits just after the report id and echoed command.
            byte statusByte = buf[3];
            int level = statusByte & 0x0F;
            if (level is < 0 or > 10) return null; // not the frame we expected

            int percent = Math.Clamp((int)Math.Round(level * 10.0), 0, 100);
            bool charging = (statusByte & 0x10) != 0;

            return new BatteryReading
            {
                Id = $"astro:{path}",
                Name = FriendlyName(dev),
                Kind = DeviceKind.Headset,
                Percentage = percent,
                IsCharging = charging,
                IsConnected = true,
            };
        }
    }

    private static string FriendlyName(HidDevice dev)
    {
        try
        {
            string n = dev.GetProductName();
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
        }
        catch { }
        return "Astro A50";
    }
}
