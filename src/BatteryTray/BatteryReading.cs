namespace BatteryTray;

public enum DeviceKind
{
    Unknown,
    XboxController,
    Keyboard,
    Mouse,
}

/// <summary>
/// A single battery snapshot for one physical device.
/// <para><see cref="Percentage"/> is an exact 0-100 value when the device reports it
/// (Logitech HID++). For devices that only report coarse buckets (Xbox/XInput), the
/// percentage is an approximation used for the tray icon while <see cref="LevelLabel"/>
/// carries the real, human-facing text ("Full", "Low", ...).</para>
/// </summary>
public sealed record BatteryReading
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }

    /// <summary>0-100. Exact for HID++ devices, approximate for coarse (Xbox) devices.</summary>
    public int? Percentage { get; init; }

    /// <summary>Set when the source only knows a coarse level (e.g. Xbox "Full"/"Low").</summary>
    public string? LevelLabel { get; init; }

    /// <summary>
    /// False when no real battery level is available — e.g. an Xbox controller on a USB
    /// cable ("Wired"), where the percentage is a placeholder. Such a reading must not be
    /// shown as a battery level once it goes stale.
    /// </summary>
    public bool LevelKnown { get; init; } = true;

    public bool IsCharging { get; init; }
    public bool IsConnected { get; init; }

    /// <summary>Value used for sorting / picking the "worst" battery for the tray icon.</summary>
    public int SortValue => Percentage ?? (IsConnected ? 50 : 999);

    public string DisplayStatus()
    {
        if (!IsConnected)
            return "not connected";

        string core;
        if (!LevelKnown)
            core = LevelLabel ?? "plugged in";
        else if (Percentage is int p)
            core = LevelLabel is null ? $"{p}%" : $"{LevelLabel} (~{p}%)";
        else
            core = LevelLabel ?? "unknown";

        if (IsCharging)
            core += " ⚡";

        return core;
    }

    public string KindGlyph => Kind switch
    {
        DeviceKind.XboxController => "🎮",
        DeviceKind.Keyboard => "⌨",
        DeviceKind.Mouse => "🖱",
        _ => "•",
    };
}

public interface IBatterySource
{
    string Name { get; }

    /// <summary>
    /// Polls the source. Must not throw for expected conditions (device asleep,
    /// unplugged); return an empty list or a disconnected reading instead.
    /// </summary>
    IReadOnlyList<BatteryReading> Poll();
}
