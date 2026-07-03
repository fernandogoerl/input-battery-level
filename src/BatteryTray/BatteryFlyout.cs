using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BatteryTray.Rendering;

namespace BatteryTray;

/// <summary>
/// A small borderless panel that shows each device's battery — the "tooltip view" opened by
/// a left-click on the tray icon. It auto-closes when it loses focus (click away) and can be
/// live-updated while open. This is intentionally separate from the right-click action menu.
/// </summary>
public sealed class BatteryFlyout : Form
{
    private const int Width_ = 320;
    private const int TitleH = 34;
    private const int RowH = 46;
    private const int PadX = 14;
    private const int PadBottom = 12;

    private IReadOnlyList<Row> _rows = Array.Empty<Row>();
    private bool _dark = true;

    /// <param name="Value">Right-hand text: "81%", "Full", "Wired", or "—".</param>
    /// <param name="BarPercent">Fill fraction for the bar, or null for no fill.</param>
    /// <param name="ShowBolt">Draw the charging bolt (already false for stale rows).</param>
    public sealed record Row(string Name, string Value, int? BarPercent, string Status,
        bool IsStale, bool ShowBolt);

    public BatteryFlyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Width = Width_;
        Height = TitleH + RowH + PadBottom;
        BackColor = Color.FromArgb(32, 32, 32);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080; // keep out of Alt+Tab
            const int WS_EX_TOPMOST = 0x00000008;
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => false; // must activate so Deactivate fires

    public void SetRows(IReadOnlyList<Row> rows)
    {
        _rows = rows;
        _dark = !SystemUsesLightTheme();
        BackColor = _dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);

        int rowCount = Math.Max(1, rows.Count);
        Height = TitleH + rowCount * RowH + PadBottom;
        ApplyRoundedRegion();
        Invalidate();
    }

    /// <summary>Position the panel just above the tray (bottom-right of the working area).</summary>
    public void PositionNear(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = wa.Right - Width - 12;
        int y = wa.Bottom - Height - 12;
        Location = new Point(Math.Max(wa.Left + 4, x), Math.Max(wa.Top + 4, y));
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        int r = 12, d = r * 2;
        var rect = new Rectangle(0, 0, Width, Height);
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Color text = _dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 28);
        Color sub = _dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);
        Color divider = _dark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(220, 220, 220);

        using var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var nameFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using var subFont = new Font("Segoe UI", 8f, FontStyle.Regular);
        using var pctFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        // Title
        TextRenderer.DrawText(g, "Battery", titleFont,
            new Rectangle(PadX, 8, Width - PadX * 2, 20), text, TextFormatFlags.Left);
        using (var pen = new Pen(divider))
            g.DrawLine(pen, PadX, TitleH - 1, Width - PadX, TitleH - 1);

        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, "No devices seen yet — use one to wake it", subFont,
                new Rectangle(PadX, TitleH, Width - PadX * 2, RowH), sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        int y = TitleH;
        foreach (var row in _rows)
        {
            // Name + status (left)
            TextRenderer.DrawText(g, row.Name, nameFont,
                new Rectangle(PadX, y + 6, Width - PadX * 2 - 90, 18), text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.Status, subFont,
                new Rectangle(PadX, y + 24, Width - PadX * 2 - 90, 16), sub,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // Value text (far right): percentage, coarse label, or "—".
            var pctRect = new Rectangle(Width - PadX - 44, y + (RowH - 20) / 2, 44, 20);
            Color pctColor = row.IsStale ? sub : LevelColor(row.BarPercent);
            TextRenderer.DrawText(g, row.Value, pctFont, pctRect, pctColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            // Battery bar (left of the value text)
            DrawBattery(g, new Rectangle(pctRect.Left - 8 - 44, y + (RowH - 16) / 2, 44, 16),
                row.BarPercent, row.IsStale, row.ShowBolt);

            y += RowH;
        }
    }

    private static void DrawBattery(Graphics g, Rectangle r, int? percent, bool stale, bool charging)
    {
        Color c = stale ? Color.FromArgb(140, 140, 140) : LevelColor(percent);
        float nubW = 3f;
        var body = new RectangleF(r.X, r.Y, r.Width - nubW - 1, r.Height);
        using var pen = new Pen(c, 1.4f);
        g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
        g.FillRectangle(new SolidBrush(c), r.Right - nubW, r.Y + r.Height * 0.28f, nubW, r.Height * 0.44f);

        int p = percent ?? 0;
        float inset = 2f;
        float maxW = body.Width - inset * 2;
        float w = Math.Max(0, maxW * (p / 100f));
        if (percent is not null && w > 0.5f)
            g.FillRectangle(new SolidBrush(c), body.X + inset, body.Y + inset, w, body.Height - inset * 2);

        if (charging)
        {
            using var boltFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            TextRenderer.DrawText(g, "+", boltFont, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private static Color LevelColor(int? percent) => percent switch
    {
        null => Color.FromArgb(150, 150, 150),
        <= 15 => Color.FromArgb(230, 60, 60),
        <= 35 => Color.FromArgb(235, 170, 40),
        _ => Color.FromArgb(60, 190, 90),
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }
}
