using System.Diagnostics;
using HidSharp;

namespace BtBatteryTray;

/// <summary>
/// GameSir Nova 2 Lite on its 2.4 GHz dongle (VID 3537 / PID 1098), via the vendor HID interface (usage page 0xFF7A).
/// Protocol seen in a USB capture of the official software (64-byte reports):
///   host sends  F3 01 05 02 FB         (handshake, echoed back by the dongle)
///   host sends  01 01                  -> dongle replies 01 92 D0 39 E6 A4 01 00 00 00 28 00 .. 04 02 52 ..
///   byte 18 of the reply = battery %   (0x52 = 82, confirmed working on the real controller)
/// It WRITES 3 small packets to the dongle (the same ones the official app sends), at most every 30 s.
/// </summary>
internal sealed class GameSirReader : IBatteryReader
{
    public string Name => "GameSir";
    public static bool Enabled { get; set; } = true;
    public static string LastRaw { get; private set; } = "(never queried)";

    private const int Vid = 0x3537, Pid = 0x1098;
    private const int LevelOffset = 18;
    private const int CacheMs = 30_000;
    private static readonly byte[] Handshake = { 0xF3, 0x01, 0x05, 0x02, 0xFB };
    private static readonly byte[] InfoQuery = { 0x01, 0x01 };

    private long _lastTick = -CacheMs;
    private List<BatteryDevice> _cached = new();

    public async Task<List<BatteryDevice>> ReadAsync(bool force)
    {
        if (!Enabled) return new List<BatteryDevice>();
        long now = Environment.TickCount64;
        if (!force && now - _lastTick < CacheMs) return _cached;
        _cached = await Task.Run(Read);
        _lastTick = Environment.TickCount64;
        return _cached;
    }

    private static List<BatteryDevice> Read()
    {
        var list = new List<BatteryDevice>();
        var dev = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault(IsVendorInterface);
        if (dev == null) { LastRaw = "(dongle not found)"; return list; }

        try
        {
            using var s = dev.Open();
            s.ReadTimeout = 300;
            int outLen = dev.GetMaxOutputReportLength();
            int inLen = dev.GetMaxInputReportLength();

            Send(s, outLen, Handshake);
            Wait(s, inLen, Handshake[0]);                       // handshake echo
            Send(s, outLen, InfoQuery);
            var reply = Wait(s, inLen, InfoQuery[0]);
            Send(s, outLen, Handshake);                         // the official app closes with the same packet

            if (reply == null) { LastRaw = "(no reply to info query)"; return list; }
            LastRaw = Convert.ToHexString(reply);

            // bytes 1-5 look like the controller's address: all zero = controller not linked to the dongle
            bool linked = reply.Skip(1).Take(5).Any(b => b != 0);
            int level = reply[LevelOffset];
            if (linked && level is >= 1 and <= 100)
                list.Add(new BatteryDevice("gamesir-nova2lite", "GameSir Nova 2 Lite", level, "GameSir", DeviceKind.Gamepad));
        }
        catch (Exception ex) { LastRaw = "(error: " + ex.GetType().Name + ")"; }
        return list;
    }

    private static bool IsVendorInterface(HidDevice d)
    {
        try
        {
            var desc = d.GetRawReportDescriptor();
            return desc.Length >= 3 && desc[0] == 0x06 && desc[1] == 0x7A && desc[2] == 0xFF;
        }
        catch { return false; }
    }

    private static void Send(HidStream s, int outLen, byte[] cmd)
    {
        var buf = new byte[outLen];                 // buf[0] = report ID (0: this interface has none)
        Array.Copy(cmd, 0, buf, 1, cmd.Length);
        s.Write(buf);
    }

    /// <summary>Reads until a packet whose first byte is 'first' arrives (the dongle also streams F4 status packets).</summary>
    private static byte[]? Wait(HidStream s, int inLen, byte first)
    {
        var buf = new byte[inLen];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            try
            {
                int n = s.Read(buf, 0, buf.Length);
                int off = buf[0] == 0 ? 1 : 0;      // skip the leading report-ID byte when present
                if (n > off && buf[off] == first)
                {
                    var payload = new byte[n - off];
                    Array.Copy(buf, off, payload, 0, payload.Length);
                    return payload;
                }
            }
            catch (TimeoutException) { }
        }
        return null;
    }
}
