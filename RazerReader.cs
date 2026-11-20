using System.Diagnostics;
using HidSharp;

namespace BtBatteryTray;

/// <summary>
/// Razer mice and keyboards on a 2.4 GHz receiver (VID 1532), via the standard 90-byte Razer HID feature report.
/// Request/reply layout (OpenRazer's "razer_report", used unchanged by Synapse for years):
///   byte 0  = status (0x00 request / 0x02 success on reply)
///   byte 2  = transaction id (device-specific; a handful of values cover most mice, see TransactionIds)
///   byte 5  = data size (2 for these commands)
///   byte 6  = command class  (0x07 = Power)
///   byte 7  = command id     (0x80 = GET_BATTERY_LEVEL, 0x84 = GET_CHARGING_STATUS)
///   byte 8  = argument (0 for a get)
///   byte 89 = CRC (XOR of bytes 2..87), required or the device ignores the report
///   reply byte 9 = battery raw (0-255, divide by 2.55 for %) / charging reply byte 9 = 0 or 1
/// Newer "DeathAdder V3 HyperSpeed" style mice use the same class/command but transaction id 0x1F
/// and wrap the battery value with 2 leading zero bytes (observed by community captures, see HaloBattery notes).
///
/// Headsets (Barracuda Pro, BlackShark V2 Pro 2023) do NOT use this protocol: they speak a different
/// "PA" framed protocol and are not implemented here.
///
/// 100% passive except for the GET command itself, which Razer's own software sends on every poll.
/// Sources (protocol only): openrazer/openrazer (GPL-2.0, razermouse_driver.c), hyperb1iss/uchroma protocol
/// reference, xzeldon/razer-battery-report and heiland/razerbatterytaskbar (MIT-style, device/transaction tables).
/// UNCONFIRMED on real hardware: review with a real device before relying on it.
/// </summary>
internal sealed class RazerReader : IBatteryReader
{
    private const string SourceName = "Razer";
    public string Name => SourceName;
    private const int Vid = 0x1532;
    private const long CacheMs = 20_000;

    // PID -> (friendly name, transaction id). Extend freely; 0x1F covers most current wireless mice.
    private static readonly Dictionary<int, (string Name, byte Tid)> Known = new()
    {
        [0x00C5] = ("Razer DeathAdder V3 HyperSpeed", 0x1F),
        [0x00B7] = ("Razer DeathAdder V3 Pro", 0x1F),
        [0x0085] = ("Razer Basilisk Ultimate", 0x3F),
        [0x00AF] = ("Razer Basilisk V3 Pro", 0x1F),
        [0x007C] = ("Razer DeathAdder V2 Pro", 0x1F),
        [0x008E] = ("Razer Viper Ultimate", 0x1F),
        [0x00A5] = ("Razer Naga Pro", 0x1F),
        [0x00E6] = ("Razer Viper V4 Pro", 0x1F),
    };

    private long _lastTick = -CacheMs;
    private List<BatteryDevice> _cached = new();

    public async Task<List<BatteryDevice>> ReadAsync(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastTick < CacheMs) return _cached;
        _cached = await Task.Run(Read);
        _lastTick = Environment.TickCount64;
        return _cached;
    }

    private static List<BatteryDevice> Read()
    {
        var list = new List<BatteryDevice>();
        foreach (var d in DeviceList.Local.GetHidDevices(Vid))
        {
            if (d.GetMaxFeatureReportLength() < 90) continue;    // not the vendor control interface
            byte tid = 0x1F;
            string name = "Razer device";
            if (Known.TryGetValue(d.ProductID, out var info)) { name = info.Name; tid = info.Tid; }

            try
            {
                using var s = d.Open();
                int level = GetByte(s, tid, 0x80);     // Power class, GET_BATTERY_LEVEL
                if (level < 0) continue;
                int chg = GetByte(s, tid, 0x84);       // GET_CHARGING_STATUS
                int pct = (int)Math.Round(level / 2.55);
                list.Add(new BatteryDevice($"razer:{d.ProductID:X4}", name, Math.Clamp(pct, 0, 100), SourceName,
                                            Charging: chg == 1));
            }
            catch { }
        }
        return list;
    }

    private static int GetByte(HidStream s, byte tid, byte commandId)
    {
        var report = BuildRequest(tid, 0x07, commandId, 2);
        try
        {
            s.SetFeature(report);   // see HidStream stub note: real HidSharp exposes SetFeature/GetFeature on HidStream
            Thread.Sleep(25);    // the device needs a short moment before the reply is ready, per OpenRazer
            var reply = new byte[90];
            s.GetFeature(reply);
            if (reply[0] != 0x02) return -1;             // 0x02 = command successful
            if (reply[2] != tid) return -1;
            return reply[9];
        }
        catch { return -1; }
    }

    private static byte[] BuildRequest(byte tid, byte classId, byte commandId, byte size)
    {
        var r = new byte[90];
        r[2] = tid;
        r[5] = size;
        r[6] = classId;
        r[7] = commandId;
        byte crc = 0;
        for (int i = 2; i <= 87; i++) crc ^= r[i];
        r[88] = crc;
        return r;
    }
}
