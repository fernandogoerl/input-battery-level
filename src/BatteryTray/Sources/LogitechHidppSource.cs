using HidSharp;
using HidSharp.Reports;

namespace BatteryTray.Sources;

/// <summary>
/// Reads Logitech wireless device battery by speaking the HID++ 2.0 protocol directly
/// to each Lightspeed / Bolt / Unifying receiver over raw HID. No Logitech G HUB needed.
///
/// <para>Strategy per receiver interface (the one that exposes the vendor "long" report
/// 0x11 on usage page 0xFF00):</para>
/// <list type="number">
///   <item>Ping paired device indices to find the connected device.</item>
///   <item>Resolve a battery feature: UnifiedBattery (0x1004), then BatteryVoltage
///         (0x1001), then legacy BatteryStatus (0x1000).</item>
///   <item>Resolve DeviceNameAndType (0x0005) for a friendly name + device kind.</item>
///   <item>Read the charge and report it.</item>
/// </list>
/// This mirrors what Solaar / libratbag do; the feature IDs and message layout are from
/// the public Logitech HID++ 2.0 specification.
/// </summary>
public sealed class LogitechHidppSource : IBatterySource
{
    public string Name => "Logitech (HID++)";

    private const int LogitechVendorId = 0x046D;

    private const byte ReportIdShort = 0x10; // 7 bytes total
    private const byte ReportIdLong = 0x11;  // 20 bytes total

    // Arbitrary non-zero software id placed in the low nibble of the function byte so we
    // can distinguish our replies from spontaneous device notifications (which use 0).
    private const byte SoftwareId = 0x05;

    // Feature IDs (HID++ 2.0)
    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureDeviceNameType = 0x0005;
    private const ushort FeatureBatteryStatus = 0x1000;   // legacy, coarse
    private const ushort FeatureBatteryVoltage = 0x1001;  // voltage -> %
    private const ushort FeatureUnifiedBattery = 0x1004;  // preferred, exact %

    private const byte RootIndex = 0x00;
    private const byte ErrorIndex20 = 0xFF; // HID++ 2.0 error carrier in the feature-index slot
    private const byte ErrorIdShort10 = 0x8F; // HID++ 1.0 error report marker

    // Cache resolved feature indices per device path so we don't re-enumerate every poll.
    private readonly Dictionary<string, DevicePlan> _plans = new();

    private sealed class DevicePlan
    {
        public byte DeviceIndex;
        public byte BatteryFeatureIndex;
        public ushort BatteryFeatureId;
        public byte NameFeatureIndex;
        public string Name = "Logitech device";
        public DeviceKind Kind = DeviceKind.Unknown;
        public DateTime ResolvedUtc;
    }

    public IReadOnlyList<BatteryReading> Poll()
    {
        var results = new List<BatteryReading>();

        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices(vendorID: LogitechVendorId);
        }
        catch
        {
            return results;
        }

        var seenPlanKeys = new HashSet<string>();

        foreach (var dev in devices)
        {
            if (!LooksLikeHidppInterface(dev))
                continue;

            string key = SafeDevicePath(dev);
            if (!seenPlanKeys.Add(key))
                continue; // a receiver can expose the long report on more than one node

            try
            {
                var reading = PollDevice(dev, key);
                if (reading is not null)
                    results.Add(reading);
            }
            catch
            {
                // Interface not actually HID++, busy, or asleep — ignore this node.
            }
        }

        return results;
    }

    private static string SafeDevicePath(HidDevice dev)
    {
        try { return dev.DevicePath; }
        catch { return $"{dev.VendorID:X4}:{dev.ProductID:X4}"; }
    }

    /// <summary>
    /// A HID++ interface exposes the 20-byte "long" vendor report (id 0x11) on usage
    /// page 0xFF00. We accept anything that carries an output report 0x11 of the right
    /// size; the ping in <see cref="PollDevice"/> is the real confirmation.
    /// </summary>
    private static bool LooksLikeHidppInterface(HidDevice dev)
    {
        try
        {
            if (dev.GetMaxOutputReportLength() < 20 || dev.GetMaxInputReportLength() < 20)
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            var descriptor = dev.GetReportDescriptor();
            bool hasLongOutput = descriptor.Reports.Any(r =>
                r.ReportType == ReportType.Output && r.ReportID == ReportIdLong);
            bool hasVendorUsage = descriptor.DeviceItems.Any(item =>
                item.Usages.GetAllValues().Any(u => (u >> 16) == 0xFF00));

            // Require the long report; vendor usage is a strong extra hint but some
            // descriptors are terse, so don't hard-require it.
            return hasLongOutput || hasVendorUsage;
        }
        catch
        {
            // If we can't read the descriptor, fall back to the size heuristic above.
            return true;
        }
    }

    private BatteryReading? PollDevice(HidDevice dev, string key)
    {
        var opts = new OpenConfiguration();
        opts.SetOption(OpenOption.Interruptible, true);

        if (!dev.TryOpen(opts, out HidStream stream))
            return null;

        using (stream)
        {
            stream.ReadTimeout = 600;
            stream.WriteTimeout = 600;

            int outLen = dev.GetMaxOutputReportLength();
            int inLen = dev.GetMaxInputReportLength();

            if (_plans.TryGetValue(key, out var plan) &&
                (DateTime.UtcNow - plan.ResolvedUtc) < TimeSpan.FromMinutes(10))
            {
                // Re-verify the device is still reachable before trusting the plan.
                if (Ping(stream, plan.DeviceIndex, outLen, inLen))
                    return ReadBattery(stream, plan, outLen, inLen);

                _plans.Remove(key);
            }

            // Discover which device index answers (single-device dongles usually 0x01).
            byte? liveIndex = null;
            for (byte idx = 1; idx <= 6; idx++)
            {
                if (Ping(stream, idx, outLen, inLen))
                {
                    liveIndex = idx;
                    break;
                }
            }

            if (liveIndex is not byte deviceIndex)
                return null; // nothing paired / device asleep

            var newPlan = BuildPlan(stream, deviceIndex, outLen, inLen);
            if (newPlan is null)
                return null;

            newPlan.ResolvedUtc = DateTime.UtcNow;
            _plans[key] = newPlan;
            return ReadBattery(stream, newPlan, outLen, inLen);
        }
    }

    private DevicePlan? BuildPlan(HidStream stream, byte deviceIndex, int outLen, int inLen)
    {
        byte unified = GetFeatureIndex(stream, deviceIndex, FeatureUnifiedBattery, outLen, inLen);
        byte voltage = unified != 0 ? (byte)0 : GetFeatureIndex(stream, deviceIndex, FeatureBatteryVoltage, outLen, inLen);
        byte legacy = (unified != 0 || voltage != 0) ? (byte)0 : GetFeatureIndex(stream, deviceIndex, FeatureBatteryStatus, outLen, inLen);

        ushort batteryFeatureId;
        byte batteryFeatureIndex;
        if (unified != 0) { batteryFeatureId = FeatureUnifiedBattery; batteryFeatureIndex = unified; }
        else if (voltage != 0) { batteryFeatureId = FeatureBatteryVoltage; batteryFeatureIndex = voltage; }
        else if (legacy != 0) { batteryFeatureId = FeatureBatteryStatus; batteryFeatureIndex = legacy; }
        else return null; // no battery feature -> not a device we can report on

        byte nameIndex = GetFeatureIndex(stream, deviceIndex, FeatureDeviceNameType, outLen, inLen);

        var plan = new DevicePlan
        {
            DeviceIndex = deviceIndex,
            BatteryFeatureIndex = batteryFeatureIndex,
            BatteryFeatureId = batteryFeatureId,
            NameFeatureIndex = nameIndex,
        };

        ResolveNameAndKind(stream, plan, outLen, inLen);
        return plan;
    }

    private void ResolveNameAndKind(HidStream stream, DevicePlan plan, int outLen, int inLen)
    {
        if (plan.NameFeatureIndex == 0)
            return;

        try
        {
            // func 2: getDeviceType
            var typeResp = Transact(stream, plan.DeviceIndex, plan.NameFeatureIndex, 0x02, null, outLen, inLen);
            if (typeResp is not null)
                plan.Kind = MapDeviceType(typeResp[4]);

            // func 0: getDeviceNameCount, func 1: getDeviceName(charIndex)
            var countResp = Transact(stream, plan.DeviceIndex, plan.NameFeatureIndex, 0x00, null, outLen, inLen);
            if (countResp is not null)
            {
                int count = countResp[4];
                if (count is > 0 and < 128)
                {
                    var sb = new System.Text.StringBuilder(count);
                    int read = 0;
                    while (read < count)
                    {
                        var chunk = Transact(stream, plan.DeviceIndex, plan.NameFeatureIndex, 0x01,
                            new byte[] { (byte)read }, outLen, inLen);
                        if (chunk is null) break;

                        // Name chars start at param offset 0 (buffer index 4).
                        for (int i = 4; i < chunk.Length && read < count; i++, read++)
                        {
                            byte c = chunk[i];
                            if (c == 0) { read = count; break; }
                            sb.Append((char)c);
                        }
                    }

                    string name = sb.ToString().Trim();
                    if (name.Length > 0)
                        plan.Name = name;
                }
            }
        }
        catch
        {
            // Name is cosmetic; ignore failures.
        }

        if (plan.Name == "Logitech device")
        {
            plan.Name = plan.Kind switch
            {
                DeviceKind.Keyboard => "Logitech Keyboard",
                DeviceKind.Mouse => "Logitech Mouse",
                DeviceKind.Headset => "Logitech Headset",
                DeviceKind.Gamepad => "Logitech Gamepad",
                DeviceKind.Speaker => "Logitech Speaker",
                _ => "Logitech device",
            };
        }
    }

    // HID++ 2.0 DeviceNameAndType (0x0005) getDeviceType values.
    private static DeviceKind MapDeviceType(byte type) => type switch
    {
        0 => DeviceKind.Keyboard,
        2 => DeviceKind.Keyboard,   // numpad
        3 => DeviceKind.Mouse,
        4 => DeviceKind.Mouse,      // trackpad
        5 => DeviceKind.Mouse,      // trackball
        8 => DeviceKind.Headset,
        11 => DeviceKind.Gamepad,   // joystick
        12 => DeviceKind.Gamepad,
        14 => DeviceKind.Speaker,
        _ => DeviceKind.Unknown,
    };

    private BatteryReading? ReadBattery(HidStream stream, DevicePlan plan, int outLen, int inLen)
    {
        int? percentage = null;
        string? label = null;
        bool charging = false;

        switch (plan.BatteryFeatureId)
        {
            case FeatureUnifiedBattery:
            {
                // func 1: get_status -> [stateOfCharge%, batteryLevelFlags, chargingStatus, externalPower]
                var resp = Transact(stream, plan.DeviceIndex, plan.BatteryFeatureIndex, 0x01, null, outLen, inLen);
                if (resp is null) return Disconnected(plan);

                int soc = resp[4];
                if (soc is >= 0 and <= 100)
                    percentage = soc;

                byte chargingStatus = resp[6];
                // 0 = discharging, 1 = recharging, 2 = charge complete, 3 = charging error
                charging = chargingStatus == 1 || chargingStatus == 2;
                break;
            }

            case FeatureBatteryVoltage:
            {
                // func 0: get_battery_voltage -> voltage (mV, big-endian) + flags
                var resp = Transact(stream, plan.DeviceIndex, plan.BatteryFeatureIndex, 0x00, null, outLen, inLen);
                if (resp is null) return Disconnected(plan);

                int millivolts = (resp[4] << 8) | resp[5];
                percentage = VoltageToPercent(millivolts);

                byte flags = resp[6];
                charging = (flags & 0x80) != 0; // top bit: charging
                break;
            }

            case FeatureBatteryStatus:
            {
                // func 0: get_battery -> [levelPercent, nextLevel, batteryStatus]
                var resp = Transact(stream, plan.DeviceIndex, plan.BatteryFeatureIndex, 0x00, null, outLen, inLen);
                if (resp is null) return Disconnected(plan);

                int level = resp[4];
                if (level is > 0 and <= 100)
                    percentage = level;
                else
                    label = "Reported";

                byte status = resp[6];
                charging = status is 3 or 4; // recharging / charge complete on many devices
                break;
            }
        }

        return new BatteryReading
        {
            Id = $"hidpp:{plan.Name}:{plan.DeviceIndex}",
            Name = plan.Name,
            Kind = plan.Kind,
            Percentage = percentage,
            LevelLabel = label,
            IsCharging = charging,
            IsConnected = true,
        };
    }

    private static BatteryReading Disconnected(DevicePlan plan) => new()
    {
        Id = $"hidpp:{plan.Name}:{plan.DeviceIndex}",
        Name = plan.Name,
        Kind = plan.Kind,
        Percentage = null,
        IsConnected = false,
    };

    /// <summary>Rough Li-ion discharge curve (single cell) for voltage-only devices.</summary>
    private static int VoltageToPercent(int millivolts)
    {
        // Piecewise-linear approximation of a 3.5-4.2V Li-ion curve.
        (int mv, int pct)[] curve =
        {
            (4200, 100), (4100, 92), (4000, 82), (3900, 70), (3800, 55),
            (3700, 40),  (3600, 22), (3500, 8),  (3400, 2),  (3300, 0),
        };

        if (millivolts >= curve[0].mv) return 100;
        if (millivolts <= curve[^1].mv) return 0;

        for (int i = 0; i < curve.Length - 1; i++)
        {
            var hi = curve[i];
            var lo = curve[i + 1];
            if (millivolts <= hi.mv && millivolts >= lo.mv)
            {
                double t = (double)(millivolts - lo.mv) / (hi.mv - lo.mv);
                return (int)Math.Round(lo.pct + t * (hi.pct - lo.pct));
            }
        }

        return 0;
    }

    // ---- HID++ transport ----------------------------------------------------

    private bool Ping(HidStream stream, byte deviceIndex, int outLen, int inLen)
    {
        // Root feature (index 0), func 1 = getProtocolVersion / ping. Third param is a
        // marker the device echoes back. Any valid, non-error reply means "present".
        const byte marker = 0x5A;
        var resp = Transact(stream, deviceIndex, RootIndex, 0x01,
            new byte[] { 0x00, 0x00, marker }, outLen, inLen);

        return resp is not null && resp.Length > 6 && resp[6] == marker;
    }

    private byte GetFeatureIndex(HidStream stream, byte deviceIndex, ushort featureId, int outLen, int inLen)
    {
        // Root func 0 = getFeature(featureId) -> [featureIndex, featureType, featureVersion]
        var resp = Transact(stream, deviceIndex, RootIndex, 0x00,
            new byte[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) }, outLen, inLen);

        if (resp is null)
            return 0;

        return resp[4]; // 0 = feature not present
    }

    /// <summary>
    /// Sends one HID++ 2.0 request (as a long 0x11 report) and returns the matching
    /// response bytes (report id at [0], params start at [4]), or null on timeout/error.
    /// </summary>
    private byte[]? Transact(HidStream stream, byte deviceIndex, byte featureIndex, byte funcId,
        byte[]? payload, int outLen, int inLen)
    {
        var request = new byte[outLen];
        request[0] = ReportIdLong;
        request[1] = deviceIndex;
        request[2] = featureIndex;
        request[3] = (byte)((funcId << 4) | SoftwareId);
        if (payload is not null)
            Array.Copy(payload, 0, request, 4, Math.Min(payload.Length, outLen - 4));

        try
        {
            stream.Write(request);
        }
        catch
        {
            return null;
        }

        var buffer = new byte[inLen];
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(700);

        // Drain until we see our reply, skipping unrelated notifications.
        while (DateTime.UtcNow < deadline)
        {
            int n;
            try
            {
                n = stream.Read(buffer);
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch
            {
                return null;
            }

            if (n < 4)
                continue;

            byte rid = buffer[0];
            if (rid != ReportIdShort && rid != ReportIdLong)
                continue;

            if (buffer[1] != deviceIndex)
                continue;

            // HID++ 2.0 error: feature-index slot == 0xFF, echoes [featIndex, funcId, errCode]
            if (buffer[2] == ErrorIndex20)
                return null;

            // HID++ 1.0 error report
            if (rid == ReportIdShort && buffer[2] == ErrorIdShort10)
                return null;

            // Match feature index and our software id in the function byte.
            if (buffer[2] == featureIndex && (buffer[3] & 0x0F) == SoftwareId)
            {
                var copy = new byte[buffer.Length];
                Array.Copy(buffer, copy, buffer.Length);
                return copy;
            }
            // Otherwise it's a stale/notification frame; keep draining.
        }

        return null;
    }
}
