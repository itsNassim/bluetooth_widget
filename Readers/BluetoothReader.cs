using Windows.Devices.Enumeration;

namespace BtBatteryTray;

/// <summary>Paired + connected Bluetooth devices (classic and BLE) that report a battery level to Windows.</summary>
internal sealed class BluetoothReader : IBatteryReader
{
    public string Name => "Bluetooth";

    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private static readonly string[] ProtocolIds =
    {
        "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", // Bluetooth classic
        "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}", // Bluetooth LE
    };

    private const int CacheMs = 30_000;
    private long _lastTick = -CacheMs;
    private List<BatteryDevice> _cached = new();
    private DeviceWatcher? _watcher;

    public async Task<List<BatteryDevice>> ReadAsync(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastTick < CacheMs) return _cached;

        var result = new Dictionary<string, BatteryDevice>();
        string[] props = { BatteryKey, ConnectedKey };

        foreach (var proto in ProtocolIds)
        {
            string aqs = $"System.Devices.Aep.ProtocolId:=\"{proto}\" AND " +
                         "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

            var infos = await DeviceInformation.FindAllAsync(aqs, props, DeviceInformationKind.AssociationEndpoint);
            foreach (var d in infos)
            {
                bool connected = d.Properties.TryGetValue(ConnectedKey, out var c) && c is true;
                if (!connected) continue;
                if (d.Properties.TryGetValue(BatteryKey, out var b) && b is byte level)
                    result[d.Id] = new BatteryDevice(d.Id, d.Name, level, Name, GuessKind(d.Name));
            }
        }
        _cached = result.Values.ToList();
        _lastTick = now;
        return _cached;
    }

    /// <summary>Windows tells us when a Bluetooth device connects, disconnects or updates its battery.</summary>
    public void StartWatching(Action onChange)
    {
        try
        {
            string aqs = "(System.Devices.Aep.ProtocolId:=\"" + ProtocolIds[0] + "\" OR System.Devices.Aep.ProtocolId:=\"" + ProtocolIds[1] + "\")";
            _watcher = DeviceInformation.CreateWatcher(aqs, new[] { BatteryKey, ConnectedKey }, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, _) => onChange();
            _watcher.Updated += (_, _) => onChange();
            _watcher.Removed += (_, _) => onChange();
            _watcher.Start();
        }
        catch { /* polling still works */ }
    }

    internal static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (Has(n, "mouse", "mx master", "mx anywhere", "trackball")) return DeviceKind.Mouse;
        if (Has(n, "keyboard", "keychron", "mx keys")) return DeviceKind.Keyboard;
        if (Has(n, "controller", "gamepad", "dualsense", "dualshock", "xbox", "joy-con", "joycon", "pro controller", "wireless controller")) return DeviceKind.Gamepad;
        if (Has(n, "headset", "headphone", "earbud", "buds", "airpods", "earphone", "arctis", "wh-", "wf-", "soundcore", "jbl", "speaker")) return DeviceKind.Headset;
        return DeviceKind.Unknown;
    }

    private static bool Has(string s, params string[] words) => words.Any(s.Contains);
}
