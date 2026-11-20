using System.Diagnostics;
using HidSharp;

namespace BtBatteryTray;

/// <summary>
/// Logitech mice, keyboards and headsets on a Unifying, Lightspeed or Bolt receiver (VID 046D), via HID++.
/// 100% passive reads (GET requests only, nothing is paired, renamed or reconfigured).
///
/// HID++ short reports are 7 bytes: 10 DeviceIndex SubId Addr P0 P1 P2 (DeviceIndex: 0xFF = receiver itself,
/// 1-6 = a device behind it). Long reports are 20 bytes, report id 0x11, same shape, bigger parameters.
///
/// HID++ 2.0 (modern devices, G HUB/newer Unifying/Lightspeed/Bolt): feature-based.
///   0x00 GetFeature(0x1000) -> the feature index of BATTERY STATUS (sometimes 0x1001/0x1004 instead)
///   That feature's function 0 replies: byte0=% byte1=next level byte2=status(0=discharging,1=charging,
///   2=slow charging,3=complete,4=watching,7=invalid) -- numbers per the public HID++ 2.0 spec notes and Solaar.
/// HID++ 1.0 (older devices): register 0x0D (exact %) or, if that errors, register 0x07
///   (coarse level: 0..7 mapped to roughly 0,15,30,45,60,75,90,100%).
///
/// Sources (protocol only, no code copied): pwr-Solaar/Solaar (GPL-2.0, logitech_receiver/hidpp10.py and
/// hidpp20.py), HeyOkay/HaloBattery (MIT, providers/logitech.py), lekensteyn.nl/logitech-unifying.html.
/// UNCONFIRMED on real hardware: review with a real device before relying on it.
/// </summary>
internal sealed class LogitechReader : IBatteryReader
{
    private const string SourceName = "Logitech";
    public string Name => SourceName;
    private const int Vid = 0x046D;
    private const long CacheMs = 30_000;

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
        HidDevice? shortIf = null, longIf = null;
        foreach (var d in DeviceList.Local.GetHidDevices(Vid))
        {
            if (!TryUsage(d, out int page, out int usage) || page != 0xFF00) continue;
            if (usage == 0x0001) shortIf = d;
            else if (usage == 0x0002) longIf = d;
        }
        var io = longIf ?? shortIf;     // long interface can also carry short reports; short-only receivers exist too
        if (io == null) return list;    // no Logitech receiver present

        try
        {
            using var s = io.Open();
            s.ReadTimeout = 400;

            // ask the receiver which device slots (1-6) are connected: GET_CONNECTION_STATE (0x02) on device 0xFF
            for (byte idx = 1; idx <= 6; idx++)
            {
                if (!DeviceLikelyPresent(s, idx)) continue;
                var dev = ReadOneDevice(s, idx);
                if (dev != null) list.Add(dev);
            }
        }
        catch { }
        return list;
    }

    private static bool DeviceLikelyPresent(HidStream s, byte idx)
    {
        // a "ping" (root GetProtocolVersion / HID++ 1.0 error check) on an empty slot returns ERROR_UNKNOWN_DEVICE (0x8F ... 0x01)
        var err = Send(s, Short(idx, 0x00, 0x10, 0x00, 0x00, 0x1A));   // HID++ 2.0 root ping, software id 0xA
        if (err == null) return false;
        if (err[0] == 0x11 && err[2] == 0xFF) return false;            // long error report, ignore (unexpected shape)
        if (err[0] == 0x10 && err[2] == 0x8F) return false;            // HID++ 1.0 error: device absent
        return true;
    }

    private static BatteryDevice? ReadOneDevice(HidStream s, byte idx)
    {
        // --- try HID++ 2.0 first: find the Battery Status feature (0x1000) via the root feature (index 0)
        var ping = Send(s, Short(idx, 0x00, 0x10, 0x00, 0x00, 0x1A));
        bool isV2 = ping != null && ping[0] == 0x11 && ping.Length >= 5 && ping[4] >= 2;   // reply carries protocol major version

        if (isV2)
        {
            byte? feature = FindFeature(s, idx, 0x1000) ?? FindFeature(s, idx, 0x1004) ?? FindFeature(s, idx, 0x1001);
            if (feature is byte fi)
            {
                var r = Send(s, Short(idx, fi, 0x00, 0x00, 0x00, 0x00));
                if (r != null && r[0] == 0x11 && r.Length >= 7)
                {
                    int pct = r[4];
                    int status = r[6];
                    if (pct is >= 0 and <= 100)
                        return new BatteryDevice($"logi:{idx}", "Logitech device " + idx, pct, SourceName, DeviceKind.Unknown,
                                                  Charging: status is 1 or 2);
                }
            }
        }

        // --- fall back to HID++ 1.0 registers: 0x0D (exact %) then 0x07 (coarse 0-7 step)
        var exact = Send(s, Short(idx, 0x81, 0x0D, 0x00, 0x00, 0x00));     // GET_LONG_REGISTER would reply 0x11; try short GET first
        if (exact != null && exact[0] == 0x10 && exact[2] == 0x0D)
        {
            int pct = exact[3];
            if (pct is >= 0 and <= 100) return new BatteryDevice($"logi:{idx}", "Logitech device " + idx, pct, SourceName);
        }

        var coarse = Send(s, Short(idx, 0x81, 0x07, 0x00, 0x00, 0x00));
        if (coarse != null && coarse[0] == 0x10 && coarse[2] == 0x07)
        {
            int step = coarse[3] & 0x07;
            int pct = step switch { 7 => 100, 6 => 90, 5 => 75, 4 => 60, 3 => 45, 2 => 30, 1 => 15, _ => 0 };
            bool charging = (coarse[4] & 0x03) != 0;
            return new BatteryDevice($"logi:{idx}", "Logitech device " + idx, pct, SourceName, Approx: true, Charging: charging);
        }
        return null;
    }

    /// <summary>HID++ 2.0 feature discovery: ask feature 0 ("FEATURE SET"), function 0, for the index of featureId.</summary>
    private static byte? FindFeature(HidStream s, byte idx, ushort featureId)
    {
        byte hi = (byte)(featureId >> 8), lo = (byte)(featureId & 0xFF);
        var r = Send(s, Short(idx, 0x00, 0x00, hi, lo, 0x00));
        if (r != null && r[0] == 0x11 && r.Length >= 5 && r[4] != 0) return r[4];
        return null;
    }

    private static byte[] Short(byte idx, byte feature, byte funcSw, byte p0, byte p1, byte p2) =>
        new byte[] { 0x10, idx, feature, funcSw, p0, p1, p2 };

    private static byte[]? Send(HidStream s, byte[] report)
    {
        try
        {
            s.Write(report);
            var buf = new byte[20];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) continue;
                if (buf[1] != report[1]) continue;   // reply for a different device index: keep waiting
                return buf[..n];
            }
        }
        catch { }
        return null;
    }

    private static bool TryUsage(HidDevice d, out int page, out int usage)
    {
        page = usage = 0;
        try
        {
            var desc = d.GetRawReportDescriptor();
            if (desc.Length < 3 || desc[0] != 0x06) return false;
            page = desc[1] | (desc[2] << 8);
            if (desc.Length >= 5 && desc[3] == 0x09) usage = desc[4];
            return true;
        }
        catch { return false; }
    }
}
