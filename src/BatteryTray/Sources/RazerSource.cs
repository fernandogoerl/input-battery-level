using HidSharp;

namespace BatteryTray.Sources;

/// <summary>
/// Reads battery for Razer wireless devices (mice/keyboards on a HyperSpeed/wireless
/// dongle) by speaking the Razer HID feature-report protocol — the same 90-byte "razer
/// report" that the open-source OpenRazer driver uses.
///
/// <para>The report is sent as a HID <b>feature</b> report (report id 0) and read back:
/// <c>command_class 0x07</c> with <c>command_id 0x80</c> returns the battery level
/// (0-255), and <c>0x84</c> returns the charging flag. The device's <c>transaction_id</c>
/// varies by model, so we try a few known values and keep the first that answers.</para>
///
/// <para><b>Unverified against hardware.</b> Implemented from the public OpenRazer
/// protocol; validated only in that it is VID-guarded (0x1532) and fails closed — if no
/// Razer device answers with a plausible reply, it simply reports nothing.</para>
/// </summary>
public sealed class RazerSource : IBatterySource
{
    public string Name => "Razer";

    private const int RazerVendorId = 0x1532;

    private const int PayloadLen = 90;         // razer_report body
    private const int ReportLen = PayloadLen + 1; // + HID report-id byte (0)

    private const byte CmdClassPower = 0x07;
    private const byte CmdGetBattery = 0x80;
    private const byte CmdGetCharging = 0x84;
    private const byte StatusOk = 0x02;

    // transaction_id differs per model; these cover the common wireless mice/keyboards.
    private static readonly byte[] TransactionIds = { 0x3f, 0x1f, 0x9f, 0xff, 0x08 };

    // Remember the transaction id / device path that worked, to avoid re-probing.
    private readonly Dictionary<string, byte> _txById = new();

    public IReadOnlyList<BatteryReading> Poll()
    {
        var results = new List<BatteryReading>();

        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices(vendorID: RazerVendorId); }
        catch { return results; }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dev in devices)
        {
            int featLen;
            try { featLen = dev.GetMaxFeatureReportLength(); }
            catch { continue; }
            if (featLen < ReportLen) continue; // not the control interface

            string path;
            try { path = dev.DevicePath; } catch { continue; }
            if (!seenPaths.Add(path)) continue;

            try
            {
                var reading = PollDevice(dev, path);
                if (reading is not null)
                    results.Add(reading);
            }
            catch
            {
                // Busy / wrong interface / asleep — ignore.
            }
        }

        return results;
    }

    private BatteryReading? PollDevice(HidDevice dev, string path)
    {
        if (!dev.TryOpen(out HidStream stream))
            return null;

        using (stream)
        {
            stream.ReadTimeout = 600;
            stream.WriteTimeout = 600;

            // Try the remembered transaction id first, then the rest.
            IEnumerable<byte> order = _txById.TryGetValue(path, out var known)
                ? new[] { known }.Concat(TransactionIds)
                : TransactionIds;

            foreach (byte tx in order.Distinct())
            {
                if (!TryGetBattery(stream, tx, out int level))
                    continue;

                _txById[path] = tx;

                int percent = (int)Math.Round(level * 100.0 / 255.0);
                percent = Math.Clamp(percent, 0, 100);

                bool charging = TryGetCharging(stream, tx, out bool chg) && chg;

                string name = FriendlyName(dev);
                return new BatteryReading
                {
                    Id = $"razer:{path}",
                    Name = name,
                    Kind = GuessKind(name),
                    Percentage = percent,
                    IsCharging = charging,
                    IsConnected = true,
                };
            }
        }

        return null;
    }

    private static bool TryGetBattery(HidStream stream, byte tx, out int level)
    {
        level = 0;
        var resp = Transact(stream, tx, CmdClassPower, CmdGetBattery, dataSize: 0x02);
        if (resp is null) return false;

        // resp is the 91-byte buffer: [0]=report id, then the 90-byte payload.
        // status = payload[0] = resp[1]; battery = arguments[1] = payload[9] = resp[10].
        if (resp[1] != StatusOk) return false;
        level = resp[10];
        // A powered-off / not-really-there device commonly answers 0 with an OK-looking
        // frame on the wrong interface; treat a flat 0 as "no reading" to avoid false 0%.
        return level > 0;
    }

    private static bool TryGetCharging(HidStream stream, byte tx, out bool charging)
    {
        charging = false;
        var resp = Transact(stream, tx, CmdClassPower, CmdGetCharging, dataSize: 0x02);
        if (resp is null || resp[1] != StatusOk) return false;
        charging = resp[10] != 0;
        return true;
    }

    /// <summary>Builds a razer report, sends it as a feature report, reads the reply.</summary>
    private static byte[]? Transact(HidStream stream, byte tx, byte cmdClass, byte cmdId, byte dataSize)
    {
        var buf = new byte[ReportLen]; // buf[0] = report id 0
        // payload starts at buf[1]
        buf[1 + 1] = tx;         // transaction_id
        buf[1 + 5] = dataSize;   // data_size
        buf[1 + 6] = cmdClass;   // command_class
        buf[1 + 7] = cmdId;      // command_id
        buf[1 + 88] = Crc(buf);  // crc over payload[2..87]

        try
        {
            stream.SetFeature(buf);
        }
        catch
        {
            return null;
        }

        // Razer firmware needs a short beat before the reply is ready.
        Thread.Sleep(60);

        var reply = new byte[ReportLen];
        reply[0] = 0; // report id
        try
        {
            stream.GetFeature(reply);
        }
        catch
        {
            return null;
        }

        return reply;
    }

    /// <summary>XOR of payload bytes [2..87] (i.e. buffer [3..88]).</summary>
    private static byte Crc(byte[] buf)
    {
        byte crc = 0;
        for (int i = 1 + 2; i <= 1 + 87; i++)
            crc ^= buf[i];
        return crc;
    }

    private static string FriendlyName(HidDevice dev)
    {
        try
        {
            string n = dev.GetProductName();
            if (!string.IsNullOrWhiteSpace(n))
                return n.Trim();
        }
        catch { }
        return "Razer device";
    }

    private static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("keyboard") || n.Contains("blackwidow") || n.Contains("huntsman") ||
            n.Contains("ornata") || n.Contains("cynosa")) return DeviceKind.Keyboard;
        if (n.Contains("headset") || n.Contains("kraken") || n.Contains("barracuda") ||
            n.Contains("nari")) return DeviceKind.Headset;
        if (n.Contains("mouse") || n.Contains("deathadder") || n.Contains("viper") ||
            n.Contains("basilisk") || n.Contains("naga") || n.Contains("cobra") ||
            n.Contains("orochi")) return DeviceKind.Mouse;
        return DeviceKind.Unknown;
    }
}
