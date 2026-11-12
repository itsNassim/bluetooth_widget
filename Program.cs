using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32;

namespace BtBatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "BtBatteryTray_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private const int LowThreshold = 20;
    private const int RearmThreshold = 25;
    private const int RefreshSeconds = 10;     // cheap: most readers cache, push readers cost nothing
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BtBatteryTray";
    private const string SettingsKey = @"Software\BtBatteryTray";

    private readonly AttackSharkReader _attackShark = new();
    private readonly IBatteryReader[] _readers;     // add new readers here (Logitech HID++, Razer, ...)

    private readonly NotifyIcon _tray;
    private readonly TaskbarWidget _widget;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly SynchronizationContext _sync;
    private readonly HashSet<string> _alerted = new();

    private IntPtr _currentHicon = IntPtr.Zero;
    private bool _busy, _rerun;
    private bool _widgetEnabled, _trayEnabled;
    private List<BatteryDevice> _last = new();

    public TrayApp()
    {
        _readers = new IBatteryReader[] { new BluetoothReader(), new XInputReader(), new GameSirReader(), _attackShark };
        GameSirReader.Enabled = ReadInt("GameSir", 1) == 1;
        _widgetEnabled = ReadInt("Widget", 1) == 1;
        _trayEnabled = ReadInt("Tray", 1) == 1;

        _widget = new TaskbarWidget { ThemeMode = ReadInt("Theme", 0), OffsetPx = ReadInt("Offset", 0) };
        _widget.LeftClick += RequestRefresh;
        _widget.Prepare();
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = BuildMenu();
        _widget.ContextMenuStrip = menu;
        _tray = new NotifyIcon { ContextMenuStrip = menu, Text = "Battery monitor…", Visible = true };
        SetIcon("…", Color.Gray);

        // connect / disconnect => refresh right away (events are bursty, so wait 0.5 s and refresh once)
        _debounce = new System.Windows.Forms.Timer { Interval = 500 };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshAsync(true); };

        _timer = new System.Windows.Forms.Timer { Interval = RefreshSeconds * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        _timer.Start();

        DeviceList.Local.Changed += (_, _) => RequestRefresh();     // USB / HID dongles plugged or unplugged
        foreach (var r in _readers) r.StartWatching(RequestRefresh); // Bluetooth connect/disconnect, mouse frames

        _ = RefreshAsync(true);
    }

    /// <summary>Safe to call from any thread.</summary>
    private void RequestRefresh() => _sync.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null);

    // ------------------------------------------------------------------ menu
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => RequestRefresh());

        var showWidget = new ToolStripMenuItem("Show in taskbar") { Checked = _widgetEnabled };
        showWidget.Click += (_, _) =>
        {
            _widgetEnabled = showWidget.Checked = !showWidget.Checked;
            WriteInt("Widget", _widgetEnabled ? 1 : 0);
            ApplyUi(_last);
        };
        menu.Items.Add(showWidget);

        var showTray = new ToolStripMenuItem("Show tray icon") { Checked = _trayEnabled };
        showTray.Click += (_, _) =>
        {
            _trayEnabled = showTray.Checked = !showTray.Checked;
            WriteInt("Tray", _trayEnabled ? 1 : 0);
            ApplyUi(_last);
        };
        menu.Items.Add(showTray);

        var theme = new ToolStripMenuItem("Widget theme");
        string[] names = { "Follow Windows", "Dark", "Light" };
        var themeItems = new List<ToolStripMenuItem>();
        for (int i = 0; i < names.Length; i++)
        {
            int mode = i;
            var item = new ToolStripMenuItem(names[i]) { Checked = _widget.ThemeMode == mode };
            item.Click += (_, _) =>
            {
                _widget.ThemeMode = mode;
                WriteInt("Theme", mode);
                foreach (var t in themeItems) t.Checked = ReferenceEquals(t, item);
                _widget.Invalidate();
            };
            themeItems.Add(item);
            theme.DropDownItems.Add(item);
        }
        menu.Items.Add(theme);

        menu.Items.Add("Move widget left", null, (_, _) => MoveWidget(+16));
        menu.Items.Add("Move widget right", null, (_, _) => MoveWidget(-16));

        var gamesir = new ToolStripMenuItem("GameSir Nova 2 Lite via dongle") { Checked = GameSirReader.Enabled };
        gamesir.Click += (_, _) =>
        {
            GameSirReader.Enabled = gamesir.Checked = !gamesir.Checked;
            WriteInt("GameSir", GameSirReader.Enabled ? 1 : 0);
            RequestRefresh();
        };
        menu.Items.Add(gamesir);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Save device report (hid-dump.txt on Desktop)", null, (_, _) => SaveDump());
        menu.Items.Add("Listen to vendor interfaces, 15 s (hid-listen.txt)", null, async (_, _) => await ListenAsync());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsAutoStart() };
        startup.Click += (_, _) =>
        {
            SetAutoStart(!startup.Checked);
            startup.Checked = IsAutoStart();
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void MoveWidget(int delta)
    {
        _widget.OffsetPx = Math.Max(0, _widget.OffsetPx + delta);
        WriteInt("Offset", _widget.OffsetPx);
        _widget.Reposition();
    }

    // ------------------------------------------------------------------ refresh
    private async Task RefreshAsync(bool force)
    {
        if (_busy) { _rerun = true; return; }
        _busy = true;
        try
        {
            do
            {
                _rerun = false;
                var devices = await ReadAllAsync(force);
                _last = devices;
                ApplyUi(devices);
                NotifyIfLow(devices);
                force = true;      // a rerun was requested by an event: refresh everything
            } while (_rerun);
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

    private async Task<List<BatteryDevice>> ReadAllAsync(bool force)
    {
        var all = new List<BatteryDevice>();
        foreach (var reader in _readers)
        {
            try { all.AddRange(await reader.ReadAsync(force)); }
            catch { /* one broken reader must not stop the others */ }
        }
        return all.OrderBy(d => d.Name).ToList();
    }

    private void ApplyUi(List<BatteryDevice> devices)
    {
        _widget.SetDevices(_widgetEnabled ? devices : new List<BatteryDevice>());

        // the tray icon stays reachable whenever the widget cannot be used (disabled or no device)
        _tray.Visible = _trayEnabled || !_widgetEnabled || devices.Count == 0;
        if (devices.Count == 0)
        {
            SetIcon("–", Color.Gray);
            _tray.Text = "No device reporting its battery";
            return;
        }

        var lowest = devices.OrderBy(d => d.Level).First();
        SetIcon(lowest.Level.ToString(), LevelColor(lowest.Level));
        var text = string.Join("\n", devices.Select(d =>
            $"{d.Name}: {(d.Approx ? "~" : "")}{d.Level}%{(d.Charging == true ? " (charging)" : "")}"));
        _tray.Text = Truncate(text, 120);     // NotifyIcon limit is 127 chars
    }

    private static Color LevelColor(int level) =>
        level <= LowThreshold ? Color.FromArgb(220, 50, 50) :
        level <= 40 ? Color.FromArgb(235, 140, 20) : Color.FromArgb(40, 170, 80);

    private void NotifyIfLow(List<BatteryDevice> devices)
    {
        foreach (var d in devices)
        {
            if (d.Charging == true) { _alerted.Remove(d.Id); continue; }
            if (d.Level <= LowThreshold && _alerted.Add(d.Id))
                _tray.ShowBalloonTip(5000, "Low battery", $"{d.Name}: {(d.Approx ? "~" : "")}{d.Level}%", ToolTipIcon.Warning);
            else if (d.Level >= RearmThreshold)
                _alerted.Remove(d.Id);
        }
    }

    // ------------------------------------------------------------------ diagnostics
    private void SaveDump()
    {
        try
        {
            string path = HidDump.Run(_attackShark.DebugState());
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Report failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task ListenAsync()
    {
        _tray.ShowBalloonTip(4000, "Listening for 15 s", "Move the mouse and press the controller buttons now", ToolTipIcon.Info);
        try
        {
            string path = await HidListen.RunAsync();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Listen failed", ex.Message, ToolTipIcon.Error);
        }
    }

    // ------------------------------------------------------------------ tray icon drawing
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
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        }

        IntPtr hicon = bmp.GetHicon();
        using (var icon = Icon.FromHandle(hicon))
            _tray.Icon = (Icon)icon.Clone();

        if (_currentHicon != IntPtr.Zero) DestroyIcon(_currentHicon);
        _currentHicon = hicon;
    }

    // ------------------------------------------------------------------ settings + auto-start (HKCU, no admin rights)
    private static int ReadInt(string name, int def)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(name) is int v ? v : def;
    }

    private static void WriteInt(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

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

    // ------------------------------------------------------------------ memory
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
        _debounce.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _widget.Close();
        if (_currentHicon != IntPtr.Zero) DestroyIcon(_currentHicon);
        ExitThread();
        Environment.Exit(0);     // also ends the background reader threads
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);
}
