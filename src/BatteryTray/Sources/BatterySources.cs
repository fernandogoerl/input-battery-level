namespace BatteryTray.Sources;

/// <summary>
/// Central registry of every battery source. Both the tray and the <c>--diag</c> probe
/// build their source list from here, so adding support for a new device or brand means
/// adding one line in one place — the rest of the app stays device-agnostic.
/// </summary>
public static class BatterySources
{
    /// <summary>
    /// Creates a fresh instance of every source, in display order. Sources are stateful
    /// (they cache per-device plans), so callers keep the instances and re-poll them.
    /// </summary>
    public static IBatterySource[] CreateAll() => new IBatterySource[]
    {
        new LogitechHidppSource(), // Logitech wireless, exact %
        new RazerSource(),         // Razer wireless, via the OpenRazer protocol
        new CorsairSource(),       // Corsair wireless (Bragi), best-effort
        new AstroSource(),         // Astro A50 base station, best-effort
        new BluetoothBatterySource(),
        new XInputXboxSource(),    // Xbox / XInput gamepads
    };
}
