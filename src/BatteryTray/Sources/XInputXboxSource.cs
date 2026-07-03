using System.Runtime.InteropServices;

namespace BatteryTray.Sources;

/// <summary>
/// Reads Xbox controller battery via the XInput API. This is the reliable path for
/// Xbox One/Series controllers attached through the Xbox Wireless Adapter (dongle):
/// they show up as XInput slots 0-3 and expose a coarse battery level.
/// </summary>
public sealed class XInputXboxSource : IBatterySource
{
    public string Name => "Xbox (XInput)";

    private const int ERROR_SUCCESS = 0;
    private const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;

    // Battery types
    private const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    private const byte BATTERY_TYPE_WIRED = 0x01;

    // Battery levels
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
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    // xinput1_4.dll ships with Windows 8+ and is the only XInput version that exposes
    // battery information. Bind lazily so a missing DLL just disables this source.
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation(
        int dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    private static bool _available = true;

    public IReadOnlyList<BatteryReading> Poll()
    {
        if (!_available)
            return Array.Empty<BatteryReading>();

        var results = new List<BatteryReading>();

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

                var (label, approx) = wired
                    ? ("Wired", 100)
                    : MapLevel(batt.BatteryLevel);

                results.Add(new BatteryReading
                {
                    Id = $"xinput:{i}",
                    Name = $"Xbox Controller {i + 1}",
                    Kind = DeviceKind.XboxController,
                    Percentage = approx,
                    LevelLabel = label,
                    // A wired controller reports no real wireless battery level.
                    LevelKnown = !wired,
                    IsCharging = wired,
                    IsConnected = true,
                });
            }
            catch (DllNotFoundException)
            {
                _available = false;
                break;
            }
            catch (EntryPointNotFoundException)
            {
                _available = false;
                break;
            }
        }

        return results;
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
