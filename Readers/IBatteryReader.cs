namespace BtBatteryTray;

/// <summary>One detected device. Approx = the source only gives coarse levels (e.g. XInput: 4 steps).</summary>
internal sealed record BatteryDevice(string Id, string Name, int Level, string Source, bool Approx = false);

/// <summary>
/// A source of battery information. To support a new brand/protocol, write one class
/// implementing this interface and add it to the list in TrayApp.
/// </summary>
internal interface IBatteryReader
{
    string Name { get; }
    Task<List<BatteryDevice>> ReadAsync();
}
