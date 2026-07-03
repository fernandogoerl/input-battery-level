using System.Text;

namespace BatteryTray;

/// <summary>
/// Scripted checks for the poll-merge logic (<see cref="TrayContext.ComputeState"/>),
/// which is what fixes "Refresh now made everything show disconnected". Run with
/// <c>BatteryTray.exe --selftest [outputFile]</c>.
/// </summary>
internal static class SelfTest
{
    public static void Run(string? outputFile)
    {
        var sb = new StringBuilder();
        int failures = 0;

        void Check(string name, bool ok)
        {
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok) failures++;
        }

        var known = new Dictionary<string, TrayContext.Known>();
        var forget = TimeSpan.FromHours(12);
        var t0 = DateTime.UtcNow;

        BatteryReading Mouse(int pct) => new()
        {
            Id = "hidpp:G502 X:1", Name = "G502 X", Kind = DeviceKind.Mouse,
            Percentage = pct, IsConnected = true,
        };

        // Round 1: mouse awake at 81%.
        var r1 = TrayContext.ComputeState(known, new() { Mouse(81) }, t0, forget);
        Check("round1: mouse shown", r1.display.Count == 1);
        Check("round1: mouse fresh (not stale)", !r1.display[0].IsStale);
        Check("round1: worst = 81", r1.worst == 81);

        // Round 2: mouse asleep -> source returns nothing. THE BUG: it used to vanish.
        var r2 = TrayContext.ComputeState(known, new(), t0.AddMinutes(1), forget);
        Check("round2: mouse STILL shown when asleep", r2.display.Count == 1);
        Check("round2: mouse marked stale/asleep", r2.display[0].IsStale);
        Check("round2: icon keeps last-known 81", r2.worst == 81);
        Check("round2: no fresh readings", r2.fresh.Count == 0);

        // Round 3: mouse wakes at a low 5% -> fresh again, eligible for low-battery alert.
        var r3 = TrayContext.ComputeState(known, new() { Mouse(5) }, t0.AddMinutes(2), forget);
        Check("round3: mouse fresh again", !r3.display[0].IsStale);
        Check("round3: worst = 5", r3.worst == 5);
        Check("round3: fresh includes low mouse", r3.fresh.Count == 1 && r3.fresh[0].Percentage == 5);

        // Round 4: absent long past the forget window -> dropped for good.
        var r4 = TrayContext.ComputeState(known, new(), t0.AddHours(13), forget);
        Check("round4: forgotten after 12h absence", r4.display.Count == 0 && known.Count == 0);

        // Disconnected readings must not be remembered as good data.
        var known2 = new Dictionary<string, TrayContext.Known>();
        var disc = new BatteryReading { Id = "x", Name = "X", IsConnected = false, Percentage = null };
        var rd = TrayContext.ComputeState(known2, new() { disc }, t0, forget);
        Check("disconnected reading is ignored", rd.display.Count == 0 && rd.fresh.Count == 0);

        sb.Insert(0, $"BatteryTray self-test — {DateTime.Now:u}\n{new string('=', 40)}\nResult: " +
                     (failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)") + "\n\n");

        string report = sb.ToString();
        if (!string.IsNullOrEmpty(outputFile))
        {
            try { File.WriteAllText(outputFile, report); } catch { }
        }

        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
