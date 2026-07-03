using System.Runtime.InteropServices;
using Windows.Devices.Power;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryTray.Sources;

/// <summary>
/// Reads Xbox controller battery from two APIs and merges them:
/// <list type="bullet">
///   <item><b>Windows.Gaming.Input</b> (<c>RawGameController.TryGetBatteryReport</c>) — the
///   modern path. Unlike XInput it exposes a <b>charging</b> state and, for rechargeable
///   packs, a real remaining/full-capacity percentage. This is what makes "plugged in to
///   charge on the dongle" actually show up.</item>
///   <item><b>XInput</b> (<c>XInputGetBatteryInformation</c>) — the reliable fallback that
///   always reports presence and a coarse bucket (Empty/Low/Medium/Full) for controllers on
///   the Xbox Wireless Adapter, even when the battery report has no capacity numbers.</item>
/// </list>
/// The two are merged positionally (the common case is a single controller): the battery %
/// prefers the exact capacity reading and falls back to the XInput bucket, while charging is
/// taken from either source. If Windows.Gaming.Input yields nothing, behaviour is exactly the
/// old XInput-only path.
/// </summary>
public sealed class XboxControllerSource : IBatterySource
{
    public string Name => "Xbox";

    public XboxControllerSource()
    {
        // Prime the controller list and keep it warm. Windows.Gaming.Input populates its
        // static list from device-arrival events, so we seed it and subscribe up front.
        try
        {
            foreach (var c in RawGameController.RawGameControllers)
                lock (_gamepads) { if (!_gamepads.Contains(c)) _gamepads.Add(c); }

            RawGameController.RawGameControllerAdded += (_, c) =>
            {
                lock (_gamepads) { if (!_gamepads.Contains(c)) _gamepads.Add(c); }
            };
            RawGameController.RawGameControllerRemoved += (_, c) =>
            {
                lock (_gamepads) { _gamepads.Remove(c); }
            };
        }
        catch
        {
            // Windows.Gaming.Input unavailable — we'll run XInput-only.
        }
    }

    private readonly List<RawGameController> _gamepads = new();

    public IReadOnlyList<BatteryReading> Poll()
    {
        var xi = ReadXInput();
        var gi = ReadGameInput();

        var results = new List<BatteryReading>();
        int count = Math.Max(xi.Count, gi.Count);
        for (int i = 0; i < count; i++)
        {
            Xi? x = i < xi.Count ? xi[i] : null;
            Gi? g = i < gi.Count ? gi[i] : null;
            if (x is null && g is null)
                continue;

            // Battery %: exact capacity reading wins, else the XInput coarse approximation.
            int? percent = g?.Percent ?? x?.Approx;
            bool wired = x?.Wired ?? false;
            bool charging = (g?.Charging ?? false) || wired;

            // A wired-as-input controller has no meaningful wireless battery level (the API
            // hands back a placeholder 100%), so always surface "Wired" and mark the level as
            // unknown — this is what drives the "—" once the reading goes stale.
            bool levelKnown = !wired;
            string? label =
                wired ? "Wired"
                : g?.Percent is not null ? null
                : x?.Label ?? (g is { Charging: true } ? "Charging" : "Connected");

            string name = x?.Name ?? g?.Name ?? $"Xbox Controller {i + 1}";

            results.Add(new BatteryReading
            {
                Id = $"xbox:{i}",
                Name = name,
                Kind = DeviceKind.Gamepad,
                Percentage = percent,
                LevelLabel = label,
                LevelKnown = levelKnown,
                IsCharging = charging,
                IsConnected = true,
            });
        }

        return results;
    }

    // ---- Windows.Gaming.Input ------------------------------------------------

    private sealed record Gi(int? Percent, bool Charging, string Name);

    private List<Gi> ReadGameInput()
    {
        var list = new List<Gi>();

        RawGameController[] snapshot;
        try { lock (_gamepads) snapshot = _gamepads.ToArray(); }
        catch { return list; }

        foreach (var c in snapshot)
        {
            BatteryReport? report;
            try { report = c.TryGetBatteryReport(); }
            catch { continue; }
            if (report is null)
                continue;

            if (report.Status == BatteryStatus.NotPresent)
                continue; // e.g. a controller with no battery info to give

            int? percent = null;
            if (report.RemainingCapacityInMilliwattHours is int remaining &&
                report.FullChargeCapacityInMilliwattHours is int full && full > 0)
            {
                percent = Math.Clamp((int)Math.Round(remaining * 100.0 / full), 0, 100);
            }

            bool charging = report.Status == BatteryStatus.Charging;
            list.Add(new Gi(percent, charging, ControllerName(c)));
        }

        return list;
    }

    private static string ControllerName(RawGameController c)
    {
        try
        {
            string n = c.DisplayName;
            if (!string.IsNullOrWhiteSpace(n))
                return n.Trim();
        }
        catch { }
        return "Xbox Controller";
    }

    // ---- XInput --------------------------------------------------------------

    private sealed record Xi(bool Wired, int Approx, string Label, string Name);

    private const int ERROR_SUCCESS = 0;
    private const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;
    private const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    private const byte BATTERY_TYPE_WIRED = 0x01;
    private const byte BATTERY_LEVEL_EMPTY = 0;
    private const byte BATTERY_LEVEL_LOW = 1;
    private const byte BATTERY_LEVEL_MEDIUM = 2;
    private const byte BATTERY_LEVEL_FULL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ulong gamepad0;   // opaque XINPUT_GAMEPAD payload; we don't read it
        public ushort gamepad1;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation(
        int dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    private static bool _xinputAvailable = true;

    private static List<Xi> ReadXInput()
    {
        var list = new List<Xi>();
        if (!_xinputAvailable)
            return list;

        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out _) != ERROR_SUCCESS)
                    continue; // slot empty

                if (XInputGetBatteryInformation(i, BATTERY_DEVTYPE_GAMEPAD, out var batt) != ERROR_SUCCESS)
                    continue;

                if (batt.BatteryType == BATTERY_TYPE_DISCONNECTED)
                    continue;

                bool wired = batt.BatteryType == BATTERY_TYPE_WIRED;
                var (label, approx) = wired ? ("Wired", 100) : MapLevel(batt.BatteryLevel);
                list.Add(new Xi(wired, approx, label, $"Xbox Controller {i + 1}"));
            }
            catch (DllNotFoundException) { _xinputAvailable = false; break; }
            catch (EntryPointNotFoundException) { _xinputAvailable = false; break; }
        }

        return list;
    }

    private static (string label, int approx) MapLevel(byte level) => level switch
    {
        BATTERY_LEVEL_EMPTY => ("Empty", 5),
        BATTERY_LEVEL_LOW => ("Low", 25),
        BATTERY_LEVEL_MEDIUM => ("Medium", 60),
        BATTERY_LEVEL_FULL => ("Full", 100),
        _ => ("Unknown", 50),
    };
}
