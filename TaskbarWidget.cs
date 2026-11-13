using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BtBatteryTray;

/// <summary>
/// Battery widget docked in the Windows 10 taskbar, just left of the system tray area.
/// "Minimal bars" style: one rounded capsule with a violet outline; per device a glyph (mouse, gamepad, headset,
/// keyboard) with a thin level bar underneath (red / orange / green) and a small bolt while charging.
/// The exact percentages are in the hover tooltip, or next to the icons when ShowPercent is on.
/// It is a child window of the taskbar (Shell_TrayWnd) so it follows the taskbar and hides with it in fullscreen;
/// if Windows refuses the re-parenting it falls back to a topmost window placed over the taskbar.
/// </summary>
internal sealed class TaskbarWidget : Form
{
    public int ThemeMode { get; set; }      // 0 = follow Windows, 1 = dark, 2 = light
    public int OffsetPx { get; set; }       // extra distance to the left of the tray area
    public bool ShowPercent { get; set; } = true;   // numbers (47%) next to the icons

    private List<BatteryDevice> _devices = new();
    private readonly System.Windows.Forms.Timer _reposition = new() { Interval = 3000 };
    private readonly uint _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
    private readonly ToolTip _tip = new();

    private static readonly Color Green = Color.FromArgb(46, 196, 105);
    private static readonly Color Orange = Color.FromArgb(255, 159, 26);
    private static readonly Color Red = Color.FromArgb(232, 65, 56);
    private static readonly Color Violet = Color.FromArgb(139, 124, 246);

    // layout, in pixels at 96 dpi
    private const float CapPadX = 12, OuterMargin = 4, CellGap = 14, GlyphCol = 24, PercentCol = 32, CapHeight = 32;

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

    internal static Color LevelColor(int level) => level <= 20 ? Red : level <= 50 ? Orange : Green;

    private float CellWidth(float s) => (GlyphCol + (ShowPercent ? PercentCol : 0)) * s;

    public void SetDevices(List<BatteryDevice> devices)
    {
        _devices = devices;
        if (devices.Count == 0) { Hide(); return; }

        _tip.SetToolTip(this, string.Join("\n", devices.Select(d =>
            $"{d.Name}: {(d.Approx ? "~" : "")}{d.Level}%{(d.Charging == true ? " (charging)" : "")}")));

        float s = DeviceDpi / 96f;
        float content = devices.Count * CellWidth(s) + (devices.Count - 1) * CellGap * s;
        Width = (int)(content + 2 * (CapPadX + OuterMargin) * s);

        Reposition();                       // re-parents into the taskbar before the first show: no flicker at (0,0)
        if (!Visible) { Show(); Reposition(); }
        Invalidate();
    }

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
        var back = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(30, 30, 34);
        var fore = light ? Color.FromArgb(20, 20, 20) : Color.White;
        var accent = light ? Color.FromArgb(109, 91, 208) : Color.FromArgb(169, 155, 255);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(back);

        float s = DeviceDpi / 96f;
        float capH = CapHeight * s;
        var cap = new RectangleF(OuterMargin * s, (Height - capH) / 2f, Width - 2 * OuterMargin * s, capH);
        using (var pen = new Pen(Violet, 1.5f * s))
        using (var path = RoundRect(cap, 10 * s))
            g.DrawPath(pen, path);

        float x = cap.Left + CapPadX * s;
        foreach (var d in _devices)
        {
            DrawCell(g, d, x, cap.Top, capH, s, back, fore, accent);
            x += CellWidth(s) + CellGap * s;
        }
    }

    private void DrawCell(Graphics g, BatteryDevice d, float x, float capTop, float capH, float s, Color back, Color fore, Color accent)
    {
        // device glyph
        float gy = capTop + 5 * s;
        DrawGlyph(g, d.Kind, new RectangleF(x + 4 * s, gy, 16 * s, 16 * s), accent, s);

        // thin level bar under the glyph (never shorter than 12 % so the lowest level stays visible)
        float bx = x + 3 * s, by = gy + 20 * s, bw = 18 * s, bh = 3 * s;
        using (var track = new SolidBrush(Color.FromArgb(55, fore)))
        using (var tp = RoundRect(new RectangleF(bx, by, bw, bh), 1.5f * s))
            g.FillPath(track, tp);
        float fw = bw * Math.Max(12, Math.Clamp(d.Level, 0, 100)) / 100f;
        using (var fb = new SolidBrush(LevelColor(d.Level)))
        using (var fp = RoundRect(new RectangleF(bx, by, fw, bh), 1.5f * s))
            g.FillPath(fb, fp);

        // bolt at the top-right corner of the glyph while charging
        if (d.Charging == true)
            DrawBolt(g, new RectangleF(x + 17 * s, capTop + 1.5f * s, 7 * s, 11 * s), back);

        // optional number to the right of the icon
        if (ShowPercent)
        {
            using var font = new Font("Segoe UI", 11 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var br = new SolidBrush(d.Level <= 20 ? Red : fore);
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(d.Level + "%", font, br, new RectangleF(x + GlyphCol * s + 1 * s, capTop, PercentCol * s, capH), sf);
        }
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
                // classic controller silhouette: shoulders, two grips, d-pad on the left, two buttons on the right
                PointF P(float px, float py) => new PointF(x + w * px, y + h * (py - 0.08f));   // shifted up: centered in the box
                using var body = new GraphicsPath();
                body.AddLine(P(.30f, .20f), P(.70f, .20f));
                body.AddBezier(P(.70f, .20f), P(.88f, .20f), P(.97f, .40f), P(.99f, .64f));
                body.AddBezier(P(.99f, .64f), P(1.0f, .86f), P(.88f, .96f), P(.80f, .86f));
                body.AddBezier(P(.80f, .86f), P(.74f, .78f), P(.70f, .66f), P(.58f, .66f));
                body.AddLine(P(.58f, .66f), P(.42f, .66f));
                body.AddBezier(P(.42f, .66f), P(.30f, .66f), P(.26f, .78f), P(.20f, .86f));
                body.AddBezier(P(.20f, .86f), P(.12f, .96f), P(.0f, .86f), P(.01f, .64f));
                body.AddBezier(P(.01f, .64f), P(.03f, .40f), P(.12f, .20f), P(.30f, .20f));
                body.CloseFigure();
                g.DrawPath(pen, body);
                g.DrawLine(pen, P(.19f, .42f), P(.35f, .42f));       // d-pad
                g.DrawLine(pen, P(.27f, .34f), P(.27f, .50f));
                using var b = new SolidBrush(c);
                float r = 1.15f * s;
                g.FillEllipse(b, x + w * .70f - r, y + h * .26f - r, 2 * r, 2 * r);   // buttons
                g.FillEllipse(b, x + w * .79f - r, y + h * .38f - r, 2 * r, 2 * r);
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
