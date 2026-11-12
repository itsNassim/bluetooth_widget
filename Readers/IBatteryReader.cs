namespace BtBatteryTray;

internal enum DeviceKind { Unknown, Mouse, Keyboard, Gamepad, Headset }

/// <summary>
/// One detected device.
/// Approx   = the source only gives coarse/approximate levels.
/// Charging = true when on external power, null when the source cannot tell.
/// </summary>
internal sealed record BatteryDevice(
    string Id, string Name, int Level, string Source,
    DeviceKind Kind = DeviceKind.Unknown, bool Approx = false, bool? Charging = null);

/// <summary>
/// A source of battery information. To support a new brand/protocol, write one class
/// implementing this interface and add it to the list in TrayApp.
/// </summary>
internal interface IBatteryReader
{
    string Name { get; }

    /// <param name="force">true = ignore any cache and query the device now (connect/disconnect events, manual refresh).</param>
    Task<List<BatteryDevice>> ReadAsync(bool force);

    /// <summary>Optional: lets a reader ask for an immediate refresh when its device changes.</summary>
    void StartWatching(Action onChange) { }
}
