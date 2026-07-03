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
            new("G502 X LIGHTSPEED", 81, null, "", false, false),
            new("G915 X LS TKL", 59, null, "asleep · 3m ago", true, false),
            new("Xbox Controller 1", 100, "Full", "charging", false, true),
            new("WH-1000XM5 headphones", 12, null, "", false, false),
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
