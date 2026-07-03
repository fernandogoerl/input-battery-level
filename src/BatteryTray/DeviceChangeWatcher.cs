namespace BatteryTray;

/// <summary>
/// Listens for Windows device-change broadcasts (<c>WM_DEVICECHANGE</c>) so the tray can
/// refresh the moment a USB device is plugged in or removed — e.g. connecting a controller
/// or a wireless dongle immediately reflects its battery instead of waiting for the timer.
///
/// <para>Implemented as a hidden top-level <see cref="NativeWindow"/>; device-node changes
/// (<c>DBT_DEVNODES_CHANGED</c>) are broadcast to top-level windows with no registration
/// required. The <see cref="Changed"/> event fires on the UI thread.</para>
/// </summary>
internal sealed class DeviceChangeWatcher : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;      // device tree changed (plug/unplug)
    private const int DBT_DEVICEARRIVAL = 0x8000;         // a device/interface arrived
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;  // a device was removed

    /// <summary>Raised (on the UI thread) when a device is added or removed.</summary>
    public event Action? Changed;

    public DeviceChangeWatcher()
    {
        // A parent-less window is top-level, so it receives the device-change broadcast.
        // It is never shown.
        CreateHandle(new CreateParams { Caption = "BatteryTrayDeviceWatcher" });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            int evt = m.WParam.ToInt32();
            if (evt is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
                Changed?.Invoke();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
