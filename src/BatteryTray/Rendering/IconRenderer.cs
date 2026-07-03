using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray.Rendering;

/// <summary>
/// Renders the tray icon: a horizontal battery glyph whose fill and color reflect the
/// "worst" connected device, with the numeric percentage overlaid when it fits.
/// </summary>
public static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <param name="percent">0-100, or null when nothing is connected.</param>
    /// <param name="dim">Icon edge in pixels (system tray is typically 16, HiDPI 32).</param>
    public static Icon Render(int? percent, bool anyCharging, int dim = 32)
    {
        using var bmp = new Bitmap(dim, dim, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            Color fg = ColorFor(percent);

            // Battery body geometry (leave a margin; reserve a nub on the right).
            float margin = dim * 0.10f;
            float nubW = dim * 0.08f;
            float bodyX = margin;
            float bodyY = dim * 0.26f;
            float bodyW = dim - margin * 2 - nubW;
            float bodyH = dim * 0.48f;
            float radius = dim * 0.08f;

            using var pen = new Pen(fg, Math.Max(1.5f, dim * 0.06f));
            var body = new RectangleF(bodyX, bodyY, bodyW, bodyH);
            using (var path = RoundedRect(body, radius))
                g.DrawPath(pen, path);

            // Positive terminal nub.
            var nub = new RectangleF(bodyX + bodyW + dim * 0.01f, bodyY + bodyH * 0.28f, nubW, bodyH * 0.44f);
            using (var nubBrush = new SolidBrush(fg))
                g.FillRectangle(nubBrush, nub);

            // Fill proportional to charge.
            int p = percent ?? 0;
            float inset = pen.Width;
            float maxFillW = bodyW - inset * 2;
            float fillW = Math.Max(0, maxFillW * (p / 100f));
            if (percent is not null && fillW > 0.5f)
            {
                var fill = new RectangleF(bodyX + inset, bodyY + inset, fillW, bodyH - inset * 2);
                using var fillBrush = new SolidBrush(fg);
                using var fillPath = RoundedRect(fill, radius * 0.5f);
                g.FillPath(fillBrush, fillPath);
            }

            if (percent is null)
            {
                // No devices: draw a dim "?".
                DrawCenteredText(g, "?", dim, Color.Gray, bodyY, bodyH);
            }
            else if (anyCharging)
            {
                // Charging bolt overlay.
                DrawBolt(g, dim, bodyX, bodyY, bodyW, bodyH);
            }
            else
            {
                // Percentage number under the battery so it stays readable at 32px.
                string text = p >= 100 ? "100" : p.ToString();
                DrawCenteredText(g, text, dim, fg, dim * 0.72f, dim * 0.28f);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawCenteredText(Graphics g, string text, int dim, Color color, float y, float h)
    {
        float fontSize = h * 0.80f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, brush, new RectangleF(0, y, dim, h), fmt);
    }

    private static void DrawBolt(Graphics g, int dim, float bx, float by, float bw, float bh)
    {
        float cx = bx + bw / 2;
        float cy = by + bh / 2;
        float s = bh * 0.9f;
        PointF[] bolt =
        {
            new(cx - s * 0.10f, cy - s * 0.55f),
            new(cx + s * 0.28f, cy - s * 0.55f),
            new(cx + s * 0.02f, cy - s * 0.05f),
            new(cx + s * 0.30f, cy - s * 0.05f),
            new(cx - s * 0.18f, cy + s * 0.60f),
            new(cx - s * 0.02f, cy + s * 0.05f),
            new(cx - s * 0.30f, cy + s * 0.05f),
        };
        using var brush = new SolidBrush(Color.White);
        using var pen = new Pen(ColorFor(0) == Color.Empty ? Color.Black : Color.FromArgb(40, 40, 40), 1f);
        g.FillPolygon(brush, bolt);
        g.DrawPolygon(pen, bolt);
    }

    private static Color ColorFor(int? percent) => percent switch
    {
        null => Color.FromArgb(150, 150, 150),
        <= 15 => Color.FromArgb(230, 60, 60),    // red
        <= 35 => Color.FromArgb(235, 170, 40),   // amber
        _ => Color.FromArgb(60, 190, 90),        // green
    };

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
