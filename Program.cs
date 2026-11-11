using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace BtBatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance
        using var mutex = new Mutex(true, "BtBatteryTray_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

internal sealed record BtDevice(string Id, string Name, int Level);

internal sealed class TrayApp : ApplicationContext
{
    // Windows exposes the battery % of paired Bluetooth devices (classic + BLE) under this key
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private static readonly string[] ProtocolIds =
    {
        "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", // Bluetooth classic
        "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}", // Bluetooth LE
    };

    private const int LowThreshold = 20;
    private const int RearmThreshold = 25;
    private const int RefreshSeconds = 60;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BtBatteryTray";

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HashSet<string> _alerted = new();
    private IntPtr _currentHicon = IntPtr.Zero;
    private bool _busy;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsAutoStart() };
        startup.Click += (_, _) =>
        {
            SetAutoStart(!startup.Checked);
            startup.Checked = IsAutoStart();
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Bluetooth battery…",
            Visible = true,
        };
        SetIcon("…", Color.Gray);

        _timer = new System.Windows.Forms.Timer { Interval = RefreshSeconds * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var devices = await ReadDevicesAsync();
            UpdateUi(devices);
            NotifyIfLow(devices);
        }
        catch (Exception ex)
        {
            _tray.Text = Truncate("Error: " + ex.Message, 120);
        }
        finally
        {
            _busy = false;
            TrimMemory();
        }
    }

    private static async Task<List<BtDevice>> ReadDevicesAsync()
    {
        var result = new Dictionary<string, BtDevice>();
        string[] props = { BatteryKey, ConnectedKey };

        foreach (var proto in ProtocolIds)
        {
            string aqs = $"System.Devices.Aep.ProtocolId:=\"{proto}\" AND " +
                         "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

            var infos = await DeviceInformation.FindAllAsync(
                aqs, props, DeviceInformationKind.AssociationEndpoint);

            foreach (var d in infos)
            {
                bool connected = d.Properties.TryGetValue(ConnectedKey, out var c) && c is true;
                if (!connected) continue;

                if (d.Properties.TryGetValue(BatteryKey, out var b) && b is byte level)
                    result[d.Id] = new BtDevice(d.Id, d.Name, level);
            }
        }
        return result.Values.OrderBy(x => x.Level).ToList();
    }

    private void UpdateUi(List<BtDevice> devices)
    {
        if (devices.Count == 0)
        {
            SetIcon("–", Color.Gray);
            _tray.Text = "No Bluetooth device with battery info";
            return;
        }

        var lowest = devices[0];
        var color = lowest.Level <= LowThreshold ? Color.FromArgb(220, 50, 50) : Color.FromArgb(40, 170, 80);
        SetIcon(lowest.Level.ToString(), color);

        var text = string.Join("\n", devices.Select(d => $"{d.Name}: {d.Level}%"));
        _tray.Text = Truncate(text, 120); // NotifyIcon limit is 127 chars
    }

    private void NotifyIfLow(List<BtDevice> devices)
    {
        foreach (var d in devices)
        {
            if (d.Level <= LowThreshold && _alerted.Add(d.Id))
                _tray.ShowBalloonTip(5000, "Low battery", $"{d.Name}: {d.Level}%", ToolTipIcon.Warning);
            else if (d.Level >= RearmThreshold)
                _alerted.Remove(d.Id);
        }
    }

    // ---- Tray icon drawing (text rendered into a 32x32 bitmap) ----
    private void SetIcon(string text, Color back)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(back);
            g.FillRectangle(brush, 0, 0, 32, 32);

            float size = text.Length >= 3 ? 15f : 22f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        }

        IntPtr hicon = bmp.GetHicon();
        using (var icon = Icon.FromHandle(hicon))
            _tray.Icon = (Icon)icon.Clone();

        if (_currentHicon != IntPtr.Zero) DestroyIcon(_currentHicon);
        _currentHicon = hicon;
    }

    // ---- Auto-start (HKCU, no admin rights needed) ----
    private static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (enable) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }

    // ---- Memory ----
    private static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (_currentHicon != IntPtr.Zero) DestroyIcon(_currentHicon);
        ExitThread();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);
}
