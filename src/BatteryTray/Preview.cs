using System.Drawing;
using System.Drawing.Imaging;

namespace BatteryTray;

/// <summary>Renders the battery flyout with sample data to a PNG, for visual verification.</summary>
internal static class Preview
{
    public static void Flyout(string pngPath)
    {
        ApplicationConfiguration.Initialize();

        using var f = new BatteryFlyout();
        f.SetRows(new List<BatteryFlyout.Row>
        {
            new("G502 X LIGHTSPEED", "81%", 81, "", false, false),
            new("G915 X LS TKL", "58%", 58, "asleep · 6m ago", true, false),
            new("Xbox Controller 1", "—", null, "asleep · 3m ago", true, false),   // stale, was wired
            new("Xbox Controller 2", "Wired", 100, "charging", false, true),       // live, wired
            new("WH-1000XM5 headphones", "12%", 12, "", false, false),
        });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Show();

        for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }

        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(pngPath, ImageFormat.Png);

        f.Close();
    }
}
