using System.Threading;

namespace BatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--diag")
        {
            Diagnostics.Run(args.Length > 1 ? args[1] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "--selftest")
        {
            SelfTest.Run(args.Length > 1 ? args[1] : null);
            return;
        }

        if (args.Length > 1 && args[0] == "--previewflyout")
        {
            Preview.Flyout(args[1]);
            return;
        }

        // Single-instance guard so the tray icon isn't duplicated.
        using var mutex = new Mutex(initiallyOwned: true, "BatteryTray_SingleInstance_9f2c", out bool isNew);
        if (!isNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());

        GC.KeepAlive(mutex);
    }
}
