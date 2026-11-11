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

    public async Task<List<BatteryDevice>> ReadAsync()
    {
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
                    result[d.Id] = new BatteryDevice(d.Id, d.Name, level, Name);
            }
        }
        return result.Values.ToList();
    }
}
