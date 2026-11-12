using System.Runtime.InteropServices;
using System.Text;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace BtBatteryTray;

/// <summary>
/// Attack Shark mice on the 2.4 GHz dongle (Xenta chip, VID 1D57; X6 / X11 / X3 / R1 ...). 100% passive.
///
/// The dongle PUSHES a 5-byte frame every ~2 s on a HID collection (usage page 0x0A, "MI_02 COL03"):
///     03 MM 40 ST NN
///     MM = model byte (0x10 on the X6, 0x55/0xE5 on X11s)
///     ST = 01 on battery | 02 external power, not charging | 03 external power, charging
///     NN = level. X6 (MM=0x10): NN x 10 = % (0x0A = 100 %). Other models: NN = %.
/// A frame is trusted only after it arrives twice in a row (the mouse sends one bogus "100 %" frame on reconnect).
/// Silence = mouse off, asleep, or in Bluetooth mode.
///
/// Sources: daeoxide/attackshark-battery-windows (MIT), gabrielmf1998/Attack-Shark-Battery-KDE (MIT),
///          blak0p/attack-shark-linux (X6 notes), confirmed against a capture of this user's X6.
/// The collection is opened with GENERIC_READ only, so nothing can ever be written to the mouse.
/// </summary>
internal sealed class AttackSharkReader : IBatteryReader
{
    public string Name => "AttackShark";

    private const int Vid = 0x1D57;
    private const long StaleMs = 8_000;     // frames come every 2 s: silence for 8 s = mouse gone/asleep

    private sealed class Listener
    {
        public string Path = "";
        public int Pid;
        public Thread? Thread;
        public volatile bool Stop;
        public SafeFileHandle? Handle;
        public int Level = -1, State, Model;
        public long LastFrameTick;
        public string LastRaw = "";
    }

    private readonly Dictionary<string, Listener> _listeners = new();
    private Action? _onChange;

    public void StartWatching(Action onChange) => _onChange = onChange;

    public Task<List<BatteryDevice>> ReadAsync(bool force)
    {
        Sync();
        var list = new List<BatteryDevice>();
        long now = Environment.TickCount64;
        lock (_listeners)
        {
            foreach (var l in _listeners.Values)
            {
                if (l.Level < 0 || now - l.LastFrameTick > StaleMs) continue;
                bool external = l.State >= 2;
                list.Add(new BatteryDevice("atk:" + l.Path, ModelName(l.Model), l.Level, Name, DeviceKind.Mouse,
                                           Approx: external, Charging: external));
            }
        }
        return Task.FromResult(list);
    }

    private static string ModelName(int model) => model switch
    {
        0x10 => "Attack Shark X6",
        0x55 or 0xE5 => "Attack Shark X11",
        _ => "Attack Shark mouse",
    };

    /// <summary>Start listening to new battery collections, stop for the ones that disappeared.</summary>
    private void Sync()
    {
        var found = new Dictionary<string, HidDevice>();
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(Vid))
                if (IsBatteryCollection(d)) found[d.DevicePath] = d;
        }
        catch { }

        lock (_listeners)
        {
            foreach (var path in _listeners.Where(kv => !found.ContainsKey(kv.Key) || kv.Value.Thread is { IsAlive: false })
                                           .Select(kv => kv.Key).ToList())
            {
                StopListener(_listeners[path]);
                _listeners.Remove(path);
            }
            foreach (var (path, d) in found)
            {
                if (_listeners.ContainsKey(path)) continue;
                var l = new Listener { Path = path, Pid = d.ProductID };
                int len = d.GetMaxInputReportLength();
                l.Thread = new Thread(() => Run(l, len)) { IsBackground = true, Name = "attackshark-" + d.ProductID.ToString("x4") };
                _listeners[path] = l;
                l.Thread.Start();
            }
        }
    }

    private static bool IsBatteryCollection(HidDevice d)
    {
        try
        {
            if (d.GetMaxInputReportLength() < 5) return false;
            var desc = d.GetRawReportDescriptor();
            if (desc.Length < 2) return false;
            bool ordinalPage = desc[0] == 0x05 && desc[1] == 0x0A;                 // usage page 0x0A
            bool vendorPage = desc.Length >= 3 && desc[0] == 0x06 && desc[2] == 0xFF; // 0xFFxx
            return ordinalPage || vendorPage;
        }
        catch { return false; }
    }

    private void Run(Listener l, int bufLen)
    {
        var h = CreateFile(l.Path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return;
        l.Handle = h;
        var buf = new byte[bufLen];
        int prevState = -1, prevRaw = -1;
        try
        {
            while (!l.Stop)
            {
                if (!ReadFile(h, buf, (uint)buf.Length, out uint n, IntPtr.Zero)) break;   // device unplugged / handle cancelled
                if (n < 5 || buf[0] != 0x03 || buf[2] != 0x40) continue;                    // not a battery frame

                int model = buf[1], state = buf[3], raw = buf[4];
                l.LastRaw = Convert.ToHexString(buf, 0, (int)n);

                bool confirmed = state == prevState && raw == prevRaw;                        // same frame twice in a row
                prevState = state; prevRaw = raw;
                if (!confirmed) continue;

                int pct = (model == 0x10 && raw <= 10) ? raw * 10 : raw;
                pct = Math.Clamp(pct, 0, 100);

                bool changed;
                lock (_listeners)
                {
                    changed = l.Level != pct || l.State != state || l.Model != model || Environment.TickCount64 - l.LastFrameTick > StaleMs;
                    l.Level = pct; l.State = state; l.Model = model;
                    l.LastFrameTick = Environment.TickCount64;
                }
                if (changed) _onChange?.Invoke();
            }
        }
        finally { h.Dispose(); }
        _onChange?.Invoke();    // listener ended (device removed): refresh the UI
    }

    private static void StopListener(Listener l)
    {
        l.Stop = true;
        var h = l.Handle;
        if (h is { IsInvalid: false, IsClosed: false }) CancelIoEx(h, IntPtr.Zero);   // unblocks the pending ReadFile
    }

    public string DebugState()
    {
        var sb = new StringBuilder();
        lock (_listeners)
        {
            if (_listeners.Count == 0) sb.AppendLine("Attack Shark: no battery collection found (VID 1D57, usage page 0x0A)");
            foreach (var l in _listeners.Values)
                sb.AppendLine($"Attack Shark PID_{l.Pid:X4}: level={l.Level} state={l.State} model=0x{l.Model:X2} lastFrame={l.LastRaw} ageMs={Environment.TickCount64 - l.LastFrameTick}");
        }
        return sb.ToString();
    }

    private const uint GenericRead = 0x80000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
