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

        // 1) Raw HID enumeration per supported vendor (helps debugging interface pick and
        //    verifying the not-yet-hardware-tested brand sources).
        (int vid, string brand)[] vendors =
        {
            (0x046D, "Logitech"),
            (0x1532, "Razer"),
            (0x1B1C, "Corsair"),
            (0x9886, "Astro"),
        };

        foreach (var (vid, brand) in vendors)
        {
            sb.AppendLine();
            sb.AppendLine($"{brand} HID interfaces (VID 0x{vid:X4}):");
            try
            {
                bool any = false;
                foreach (var dev in DeviceList.Local.GetHidDevices(vendorID: vid))
                {
                    any = true;
                    string name;
                    try { name = dev.GetFriendlyName(); } catch { name = "(no name)"; }
                    int outLen = -1, inLen = -1, featLen = -1;
                    try { outLen = dev.GetMaxOutputReportLength(); } catch { }
                    try { inLen = dev.GetMaxInputReportLength(); } catch { }
                    try { featLen = dev.GetMaxFeatureReportLength(); } catch { }
                    sb.AppendLine($"  PID 0x{dev.ProductID:X4} out={outLen,3} in={inLen,3} feat={featLen,3}  {name}");
                }
                if (!any)
                    sb.AppendLine("  (none present)");
            }
            catch (Exception ex)
            {
                sb.AppendLine("  ! enumeration failed: " + ex.Message);
            }
        }

        // 2) Run each source repeatedly, REUSING the source objects, to mirror how the
        //    tray polls (and to exercise the per-device plan cache on rounds 2+).
        var sources = BatterySources.CreateAll();

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
