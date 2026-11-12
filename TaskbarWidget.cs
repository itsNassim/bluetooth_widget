using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BtBatteryTray;

/// <summary>
/// Battery widget docked in the Windows 10 taskbar, just left of the system tray area.
/// One cell per device: [device glyph] [battery icon filled to the % with the number on top, bolt on top while charging].
/// It is a child window of the taskbar (Shell_TrayWnd) so it follows the taskbar and hides with it in fullscreen;
/// if Windows refuses the re-parenting it falls back to a topmost window placed over the taskbar.
/// </summary>
internal sealed class TaskbarWidget : Form
{
    public int ThemeMode { get; set; }      // 0 = follow Windows, 1 = dark, 2 = light
    public int OffsetPx { get; set; }       // extra distance to the left of the tray area

    private List<BatteryDevice> _devices = new();
    private readonly System.Windows.Forms.Timer _reposition = new() { Interval = 3000 };
    private readonly uint _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
    private readonly ToolTip _tip = new();

    private static readonly Color Green = Color.FromArgb(46, 196, 105);
    private static readonly Color Orange = Color.FromArgb(255, 159, 26);
    private static readonly Color Red = Color.FromArgb(232, 65, 56);

    public TaskbarWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Width = 10; Height = 40;
        _reposition.Tick += (_, _) =>
        {
            if (_devices.Count == 0) return;
            // when explorer restarts, the taskbar is destroyed together with its child windows: recreate ours
            if (!IsHandleCreated) { try { _ = Handle; } catch { return; } }
            Reposition();
        };
        _reposition.Start();
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) LeftClick?.Invoke(); };
    }

    public event Action? LeftClick;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000;     // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>Create the window handle without showing anything.</summary>
    public void Prepare() { _ = Handle; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreatedMsg) Reposition();    // explorer restarted
        base.WndProc(ref m);
    }

    public void SetDevices(List<BatteryDevice> devices)
    {
        _devices = devices;
        if (devices.Count == 0) { Hide(); return; }

        _tip.SetToolTip(this, string.Join("\n", devices.Select(d =>
            $"{d.Name}: {(d.Approx ? "~" : "")}{d.Level}%{(d.Charging == true ? " (charging)" : "")}")));

        float s = DeviceDpi / 96f;
        Width = (int)(devices.Count * CellWidth(s) + 8 * s);
        Reposition();                       // re-parents into the taskbar before the first show: no flicker at (0,0)
        if (!Visible) { Show(); Reposition(); }
        Invalidate();
    }

    private static float CellWidth(float s) => 70 * s;

    // ------------------------------------------------------------------ placement
    public void Reposition()
    {
        if (!IsHandleCreated) return;
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;
        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero) return;

        GetWindowRect(taskbar, out RECT tb);
        GetWindowRect(tray, out RECT tn);
        int tbH = tb.Bottom - tb.Top;
        if (tbH > tb.Right - tb.Left) return;            // vertical taskbar: not supported

        bool first = false;
        if (GetParent(Handle) != taskbar)
        {
            SetParent(Handle, taskbar);
            int style = GetWindowLong(Handle, -16);                         // GWL_STYLE
            style = (style & ~unchecked((int)0x80000000)) | 0x40000000;     // remove WS_POPUP, add WS_CHILD
            SetWindowLong(Handle, -16, style);
            first = true;
        }

        const uint NOZORDER = 0x0004, NOACTIVATE = 0x0010, FRAMECHANGED = 0x0020, SHOWWINDOW = 0x0040;
        if (GetParent(Handle) == taskbar)
        {
            int x = tn.Left - tb.Left - Width - OffsetPx;
            SetWindowPos(Handle, IntPtr.Zero, x, 0, Width, tbH, NOZORDER | NOACTIVATE | SHOWWINDOW | (first ? FRAMECHANGED : 0));
        }
        else   // fallback: floating topmost window over the taskbar
        {
            SetWindowPos(Handle, new IntPtr(-1), tn.Left - Width - OffsetPx, tb.Top, Width, tbH, NOACTIVATE | SHOWWINDOW);
        }
    }

    // ------------------------------------------------------------------ painting
    private bool IsLight()
    {
        if (ThemeMode == 1) return false;
        if (ThemeMode == 2) return true;
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = IsLight();
        var back = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(26, 26, 26);
        var fore = light ? Color.FromArgb(20, 20, 20) : Color.White;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(back);

        float s = DeviceDpi / 96f;
        float x = 4 * s;
        foreach (var d in _devices)
        {
            DrawCell(g, d, x, s, back, fore);
            x += CellWidth(s);
        }
    }

    private void DrawCell(Graphics g, BatteryDevice d, float x, float s, Color back, Color fore)
    {
        float cy = Height / 2f;
        Color fill = d.Level <= 20 ? Red : d.Level <= 40 ? Orange : Green;

        // device glyph
        DrawGlyph(g, d.Kind, new RectangleF(x, cy - 8 * s, 16 * s, 16 * s), fore, s);

        // battery body
        float bx = x + 21 * s, bw = 38 * s, bh = 20 * s, by = cy - bh / 2;
        var body = new RectangleF(bx, by, bw, bh);
        using (var outline = new Pen(Color.FromArgb(200, fore), 1.4f * s))
        using (var path = RoundRect(body, 4 * s))
            g.DrawPath(outline, path);
        using (var nub = new SolidBrush(Color.FromArgb(200, fore)))
            g.FillRectangle(nub, bx + bw + 1 * s, cy - 3.5f * s, 2.5f * s, 7 * s);

        // fill, proportional to the level
        float pad = 2.2f * s;
        float inner = bw - 2 * pad;
        float fw = Math.Max(inner * Math.Clamp(d.Level, 0, 100) / 100f, d.Level > 0 ? 3 * s : 0);
        if (fw > 0)
        {
            var fr = new RectangleF(bx + pad, by + pad, fw, bh - 2 * pad);
            using var fb = new SolidBrush(fill);
            using var fp = RoundRect(fr, 2.2f * s);
            g.FillPath(fb, fp);
        }

        // percentage on top of the battery (outlined for legibility), shifted right when the bolt is shown
        bool charging = d.Charging == true;
        float shift = charging ? 5 * s : 0;
        var textRect = new RectangleF(bx + shift, by, bw - shift, bh);
        using (var fam = new FontFamily("Segoe UI"))
        using (var tp = new GraphicsPath())
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            tp.AddString(d.Level.ToString(), fam, (int)FontStyle.Bold, 11 * s, textRect, sf);
            using var halo = new Pen(Color.FromArgb(210, back), 2.4f * s) { LineJoin = LineJoin.Round };
            g.DrawPath(halo, tp);
            using var tb = new SolidBrush(fore);
            g.FillPath(tb, tp);
        }

        // charging bolt on top of the battery
        if (charging)
            DrawBolt(g, new RectangleF(bx + 4 * s, by + 3 * s, 8 * s, bh - 6 * s), back);
    }

    private static void DrawBolt(Graphics g, RectangleF r, Color back)
    {
        var pts = new[]
        {
            new PointF(r.Left + r.Width * 0.60f, r.Top),
            new PointF(r.Left + r.Width * 0.10f, r.Top + r.Height * 0.56f),
            new PointF(r.Left + r.Width * 0.46f, r.Top + r.Height * 0.56f),
            new PointF(r.Left + r.Width * 0.34f, r.Bottom),
            new PointF(r.Left + r.Width * 0.92f, r.Top + r.Height * 0.40f),
            new PointF(r.Left + r.Width * 0.56f, r.Top + r.Height * 0.40f),
        };
        using var halo = new Pen(Color.FromArgb(220, back), 1.8f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(halo, pts);
        using var b = new SolidBrush(Color.FromArgb(255, 214, 10));
        g.FillPolygon(b, pts);
    }

    // ------------------------------------------------------------------ device glyphs (vector, drawn at any DPI)
    private static void DrawGlyph(Graphics g, DeviceKind kind, RectangleF r, Color c, float s)
    {
        using var pen = new Pen(c, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        switch (kind)
        {
            case DeviceKind.Mouse:
            {
                var body = new RectangleF(x + w * 0.22f, y, w * 0.56f, h);
                using var p = RoundRect(body, w * 0.28f);
                g.DrawPath(pen, p);
                g.DrawLine(pen, x + w / 2, y, x + w / 2, y + h * 0.40f);
                g.DrawLine(pen, x + w * 0.22f, y + h * 0.40f, x + w * 0.78f, y + h * 0.40f);
                break;
            }
            case DeviceKind.Gamepad:
            {
                var body = new RectangleF(x, y + h * 0.18f, w, h * 0.50f);
                using var p = RoundRect(body, h * 0.22f);
                g.DrawPath(pen, p);
                g.DrawEllipse(pen, x + w * 0.04f, y + h * 0.50f, w * 0.26f, h * 0.42f);        // left grip
                g.DrawEllipse(pen, x + w * 0.70f, y + h * 0.50f, w * 0.26f, h * 0.42f);        // right grip
                g.DrawLine(pen, x + w * 0.22f, y + h * 0.43f, x + w * 0.38f, y + h * 0.43f);   // d-pad
                g.DrawLine(pen, x + w * 0.30f, y + h * 0.33f, x + w * 0.30f, y + h * 0.53f);
                using var b = new SolidBrush(c);
                g.FillEllipse(b, x + w * 0.62f, y + h * 0.36f, 2.2f * s, 2.2f * s);            // buttons
                g.FillEllipse(b, x + w * 0.76f, y + h * 0.44f, 2.2f * s, 2.2f * s);
                break;
            }
            case DeviceKind.Headset:
            {
                g.DrawArc(pen, x + w * 0.10f, y + h * 0.02f, w * 0.80f, h * 0.86f, 180, 180);
                using var l = RoundRect(new RectangleF(x, y + h * 0.48f, w * 0.26f, h * 0.46f), w * 0.1f);
                using var rr = RoundRect(new RectangleF(x + w * 0.74f, y + h * 0.48f, w * 0.26f, h * 0.46f), w * 0.1f);
                using var b = new SolidBrush(c);
                g.FillPath(b, l);
                g.FillPath(b, rr);
                break;
            }
            case DeviceKind.Keyboard:
            {
                using var p = RoundRect(new RectangleF(x, y + h * 0.22f, w, h * 0.56f), h * 0.12f);
                g.DrawPath(pen, p);
                using var b = new SolidBrush(c);
                for (int row = 0; row < 2; row++)
                    for (int col = 0; col < 4; col++)
                        g.FillRectangle(b, x + w * (0.14f + col * 0.22f), y + h * (0.34f + row * 0.16f), 1.8f * s, 1.6f * s);
                g.DrawLine(pen, x + w * 0.26f, y + h * 0.68f, x + w * 0.74f, y + h * 0.68f);
                break;
            }
            default:   // unknown wireless device: circle with a dot
            {
                g.DrawEllipse(pen, x + w * 0.12f, y + h * 0.12f, w * 0.76f, h * 0.76f);
                using var b = new SolidBrush(c);
                g.FillEllipse(b, x + w * 0.40f, y + h * 0.40f, w * 0.20f, h * 0.20f);
                break;
            }
        }
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string s);
}
