using System.Text;
using BatteryTray.Sources;
using HidSharp;

namespace BatteryTray;

/// <summary>
/// One-shot, no-UI probe used to verify the battery sources against real hardware.
/// Run with: BatteryTray.exe --diag [outputFile]
/// </summary>
internal static class Diagnostics
{
    public static void Run(string? outputFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BatteryTray diagnostics — {DateTime.Now:u}");
        sb.AppendLine(new string('=', 60));

        // 1) Raw HID enumeration of Logitech devices (helps debugging interface pick).
        sb.AppendLine();
        sb.AppendLine("Logitech HID interfaces (VID 0x046D):");
        try
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(vendorID: 0x046D))
            {
                string name;
                try { name = dev.GetFriendlyName(); } catch { name = "(no name)"; }
                int outLen = -1, inLen = -1;
                try { outLen = dev.GetMaxOutputReportLength(); } catch { }
                try { inLen = dev.GetMaxInputReportLength(); } catch { }
                sb.AppendLine($"  PID 0x{dev.ProductID:X4} out={outLen,3} in={inLen,3}  {name}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  ! enumeration failed: " + ex.Message);
        }

        // 2) Run each source repeatedly, REUSING the source objects, to mirror how the
        //    tray polls (and to exercise the per-device plan cache on rounds 2+).
        var sources = new IBatterySource[]
        {
            new LogitechHidppSource(),
            new BluetoothBatterySource(),
            new XInputXboxSource(),
        };

        const int rounds = 4;
        for (int round = 1; round <= rounds; round++)
        {
            sb.AppendLine();
            sb.AppendLine($"---- Poll round {round}/{rounds} ----");
            foreach (var source in sources)
            {
                try
                {
                    var readings = source.Poll();
                    if (readings.Count == 0)
                    {
                        sb.AppendLine($"  {source.Name}: (no devices reported)");
                    }
                    foreach (var r in readings)
                    {
                        sb.AppendLine($"  {source.Name}: [{r.Kind}] {r.Name}: {r.DisplayStatus()} " +
                                      $"(pct={(r.Percentage?.ToString() ?? "-")}, connected={r.IsConnected})");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  {source.Name}: ! poll threw: " + ex);
                }
            }

            if (round < rounds)
                Thread.Sleep(1500);
        }

        string report = sb.ToString();
        if (!string.IsNullOrEmpty(outputFile))
        {
            try { File.WriteAllText(outputFile, report); } catch { }
        }
    }
}
