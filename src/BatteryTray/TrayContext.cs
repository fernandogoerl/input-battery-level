using BatteryTray.Rendering;
using BatteryTray.Sources;
using Microsoft.Win32;

namespace BatteryTray;

public sealed class TrayContext : ApplicationContext
{
    private const string AppName = "BatteryTray";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int LowBatteryThreshold = 15;

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly IBatterySource[] _sources;

    private readonly ToolStripMenuItem _devicesHeader;
    private readonly ToolStripMenuItem _startupItem;
    private readonly List<ToolStripItem> _deviceItems = new();

    private bool _polling;
    private Icon? _currentIcon;
    private readonly HashSet<string> _lowNotified = new();

    // Left-click battery panel ("tooltip view"), separate from the right-click action menu.
    private readonly BatteryFlyout _flyout = new();
    private DateTime _flyoutClosedAtUtc = DateTime.MinValue;
    private Point _flyoutAnchor;
    private List<DisplayItem> _lastDisplay = new();

    // Last good reading per device, so a device that goes to sleep / powers off keeps
    // showing its last known battery ("asleep") instead of vanishing. Wireless mice and
    // keyboards sleep aggressively when idle, so a poll frequently sees nothing from them.
    private readonly Dictionary<string, Known> _known = new();
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);

    internal sealed class Known
    {
        public required BatteryReading Reading;
        public DateTime LastSeenUtc;
    }

    public TrayContext()
    {
        _sources = new IBatterySource[]
        {
            new LogitechHidppSource(),
            new BluetoothBatterySource(),
            new XInputXboxSource(),
        };

        _devicesHeader = new ToolStripMenuItem("Scanning…") { Enabled = false };

        _startupItem = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup)
        {
            Checked = StartupManager.IsEnabled(AppName),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_devicesHeader);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => TriggerPoll()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Text = "Peripheral Battery — scanning…",
            Visible = true,
            ContextMenuStrip = menu, // right-click actions
            Icon = IconRenderer.Render(null, false),
        };

        // Left-click toggles the battery panel; right-click still opens the action menu.
        _tray.MouseClick += OnTrayMouseClick;
        _flyout.Deactivate += (_, _) =>
        {
            _flyoutClosedAtUtc = DateTime.UtcNow;
            _flyout.Hide();
        };

        _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _timer.Tick += (_, _) => TriggerPoll();
        _timer.Start();

        TriggerPoll();
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return; // right-click opens the action menu (handled by NotifyIcon)

        if (_flyout.Visible)
        {
            _flyout.Hide();
            return;
        }

        // Clicking the icon while the panel is open deactivates/hides it first, then fires
        // this click. Swallow that trailing click so it doesn't immediately reopen.
        if (DateTime.UtcNow - _flyoutClosedAtUtc < TimeSpan.FromMilliseconds(250))
            return;

        ShowFlyout();
    }

    private void ShowFlyout()
    {
        _flyoutAnchor = Cursor.Position;
        _flyout.SetRows(BuildRows(_lastDisplay, DateTime.UtcNow));
        _flyout.PositionNear(_flyoutAnchor);
        _flyout.Show();
        _flyout.Activate(); // ensure it can receive Deactivate to auto-close
    }

    /// <summary>
    /// Resolves what a device should actually display, accounting for staleness. A stale
    /// reading must not assert live-only facts: no charging bolt, and if we never knew a real
    /// battery level (e.g. it was on a USB cable), show "—" instead of the placeholder.
    /// </summary>
    private static (string value, int? barPercent, bool showBolt) Effective(BatteryReading r, bool stale)
    {
        bool showBolt = !stale && r.IsCharging;

        if (stale && !r.LevelKnown)
            return ("—", null, showBolt); // e.g. controller last seen wired; battery unknown now

        string value = r.LevelLabel ?? (r.Percentage is int p ? $"{p}%" : "—");
        return (value, r.Percentage, showBolt);
    }

    private static List<BatteryFlyout.Row> BuildRows(List<DisplayItem> display, DateTime now)
    {
        var rows = new List<BatteryFlyout.Row>(display.Count);
        foreach (var d in display)
        {
            var (value, barPercent, showBolt) = Effective(d.Reading, d.IsStale);
            string status = d.IsStale
                ? $"asleep · {Ago(now - d.LastSeenUtc)}"
                : (d.Reading.IsCharging ? "charging" : "");

            rows.Add(new BatteryFlyout.Row(
                Name: d.Reading.Name,
                Value: value,
                BarPercent: barPercent,
                Status: status,
                IsStale: d.IsStale,
                ShowBolt: showBolt));
        }
        return rows;
    }

    private async void TriggerPoll()
    {
        if (_polling)
            return;

        _polling = true;
        try
        {
            var readings = await Task.Run(PollAll);
            UpdateUi(readings);
        }
        catch
        {
            // Never let a poll crash the tray.
        }
        finally
        {
            _polling = false;
        }
    }

    private List<BatteryReading> PollAll()
    {
        var all = new List<BatteryReading>();
        foreach (var source in _sources)
        {
            try
            {
                all.AddRange(source.Poll());
            }
            catch
            {
                // Skip a misbehaving source this cycle.
            }
        }

        return all
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal readonly record struct DisplayItem(BatteryReading Reading, bool IsStale, DateTime LastSeenUtc);

    /// <summary>
    /// Pure merge of the latest poll into the remembered device state. Extracted so it can
    /// be unit-checked (see <c>--selftest</c>): mutates <paramref name="known"/> to remember
    /// fresh readings and forget long-absent ones, and returns what to display.
    /// </summary>
    internal static (List<DisplayItem> display, List<BatteryReading> fresh, int? worst, bool anyCharging)
        ComputeState(Dictionary<string, Known> known, List<BatteryReading> readings, DateTime now, TimeSpan forgetAfter)
    {
        // A reading counts as "fresh" only if the device answered with real data this poll.
        var fresh = readings.Where(r => r.IsConnected && r.Percentage is not null).ToList();
        var freshIds = fresh.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var r in fresh)
            known[r.Id] = new Known { Reading = r, LastSeenUtc = now };

        foreach (var gone in known.Where(kv => now - kv.Value.LastSeenUtc > forgetAfter)
                                  .Select(kv => kv.Key).ToList())
            known.Remove(gone);

        var display = known.Values
            .Select(k => new DisplayItem(k.Reading, !freshIds.Contains(k.Reading.Id), k.LastSeenUtc))
            .OrderBy(d => d.Reading.Kind)
            .ThenBy(d => d.Reading.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int? worst =
            fresh.Count > 0 ? fresh.Min(r => r.SortValue) :
            display.Count > 0 ? display.Min(d => d.Reading.SortValue) :
            (int?)null;
        if (worst is > 100) worst = null;

        bool anyCharging = fresh.Any(r => r.IsCharging);
        return (display, fresh, worst, anyCharging);
    }

    private void UpdateUi(List<BatteryReading> readings)
    {
        var now = DateTime.UtcNow;
        var (display, fresh, worst, anyCharging) = ComputeState(_known, readings, now, ForgetAfter);
        _lastDisplay = display;

        RebuildMenu(display, now);

        // If the battery panel is open, refresh its contents live.
        if (_flyout.Visible)
        {
            _flyout.SetRows(BuildRows(display, now));
            _flyout.PositionNear(_flyoutAnchor);
        }

        var newIcon = IconRenderer.Render(worst, anyCharging);
        _tray.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;

        _tray.Text = BuildTooltip(display, now);

        NotifyLowBatteries(fresh);
    }

    private void RebuildMenu(List<DisplayItem> display, DateTime now)
    {
        var menu = _tray.ContextMenuStrip!;

        // Don't reshuffle items while the user has the menu open; catch up next poll.
        if (menu.Visible)
            return;

        foreach (var item in _deviceItems)
        {
            menu.Items.Remove(item);
            item.Dispose();
        }
        _deviceItems.Clear();

        if (display.Count == 0)
        {
            _devicesHeader.Text = "No devices seen yet — use one to wake it";
            return;
        }

        _devicesHeader.Text = "Devices";
        int insertAt = menu.Items.IndexOf(_devicesHeader) + 1;
        foreach (var d in display)
        {
            var item = new ToolStripMenuItem($"{d.Reading.KindGlyph}  {d.Reading.Name}: {StatusText(d, now)}")
            {
                Enabled = false,
            };
            menu.Items.Insert(insertAt++, item);
            _deviceItems.Add(item);
        }
    }

    private static string StatusText(DisplayItem d, DateTime now)
    {
        if (!d.IsStale)
            return d.Reading.DisplayStatus();

        var (value, _, _) = Effective(d.Reading, stale: true);
        return $"{value} (asleep, {Ago(now - d.LastSeenUtc)})";
    }

    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    private static string BuildTooltip(List<DisplayItem> display, DateTime now)
    {
        if (display.Count == 0)
            return "Peripheral Battery — no devices yet";

        // NotifyIcon tooltip is capped at 127 chars; keep it terse.
        var lines = display.Select(d =>
        {
            if (!d.IsStale)
                return $"{d.Reading.Name}: {d.Reading.DisplayStatus()}";
            var (value, _, _) = Effective(d.Reading, stale: true);
            return $"{d.Reading.Name}: {value} (asleep)";
        });
        string text = "Peripheral Battery\n" + string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..127];
    }

    private void NotifyLowBatteries(List<BatteryReading> fresh)
    {
        foreach (var r in fresh)
        {
            bool isLow = r.Percentage is int p && p <= LowBatteryThreshold && !r.IsCharging;
            if (isLow && _lowNotified.Add(r.Id))
            {
                _tray.BalloonTipTitle = "Low battery";
                _tray.BalloonTipText = $"{r.Name} is at {r.DisplayStatus()}.";
                _tray.BalloonTipIcon = ToolTipIcon.Warning;
                _tray.ShowBalloonTip(5000);
            }
            else if (!isLow)
            {
                _lowNotified.Remove(r.Id); // reset so it can warn again after recovery
            }
        }
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        bool nowEnabled = !_startupItem.Checked;
        if (StartupManager.SetEnabled(AppName, nowEnabled))
            _startupItem.Checked = nowEnabled;
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        _flyout.Dispose();
        ExitThread();
    }
}

/// <summary>Manages the HKCU Run key entry for "start with Windows".</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string appName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(appName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(string appName, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(appName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
