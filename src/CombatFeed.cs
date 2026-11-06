using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Thông báo từng đòn đánh (kiểu noti): bạn gây bao nhiêu sát thương cho ai, ai gây bao nhiêu cho bạn, kỹ năng gì.
// Mỗi dòng hiện LifeSec giây rồi mờ dần. Cửa sổ riêng giống VitalsOverlay/Compass: TOPMOST, bấm xuyên khi khoá
// (chung Ctrl+F9 + độ đậm); mở khoá thì kéo để đổi vị trí, lăn chuột đổi cỡ (lưu settings.txt "feedpos").
class CombatFeed : Form
{
    readonly Func<string, string, string> loadSetting; readonly Action<string, string> saveSetting;
    readonly Timer timer;
    public bool Wanted;
    public bool Active;   // overlay radar đang mở / đang theo dõi replay quay -> mới hiện khung mẫu khi mở khoá
    public double LifeSec = 3.0;
    const double FadeSec = 0.8;          // đoạn mờ dần ở cuối đời dòng
    const int MaxLines = 6;
    bool locked = true, posDirty;
    float scale = 1f;
    Font fText, fBold;
    DateTime lastTopCheck;
    static readonly Color Key = Color.FromArgb(1, 2, 3);

    class Line { public CombatHit H; public DateTime Born; }
    readonly List<Line> lines = new List<Line>();
    bool sample;   // đang mở khoá + chưa có đòn thật -> vẽ mẫu để biết chỗ đặt

    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public CombatFeed(Func<string, string, string> load, Action<string, string> save)
    {
        loadSetting = load; saveSetting = save;
        Text = "Dino Combat Feed";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Key; TransparencyKey = Key;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Opacity = 0.9;

        string[] p = (loadSetting("feedpos", "") ?? "").Split(',');
        int px = 0, py = 0; float ps; bool ok = false;
        if (p.Length == 3 && int.TryParse(p[0], out px) && int.TryParse(p[1], out py) &&
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ps))
        {
            foreach (var s in Screen.AllScreens) if (s.WorkingArea.Contains(px + 20, py + 10)) ok = true;
            if (ok) scale = Math.Max(0.6f, Math.Min(2.5f, ps));
        }
        MakeFonts();
        ClientSize = new Size(S(400), LineH * MaxLines + S(4));
        if (ok) Location = new Point(px, py);
        else { var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Left + (wa.Width - Width) / 2 + S(150), wa.Top + (int)(wa.Height * 0.55)); }

        timer = new Timer { Interval = 50 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }

    int LineH { get { return S(22); } }
    int S(float px) { return (int)Math.Round(px * scale); }
    void MakeFonts()
    {
        if (fText != null) { fText.Dispose(); fBold.Dispose(); }
        fText = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
        fBold = new Font("Segoe UI", 9.5f * scale, FontStyle.Bold, GraphicsUnit.Point);
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            if (locked) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public bool Locked
    {
        get { return locked; }
        set
        {
            if (locked == value) return;
            locked = value;
            if (IsHandleCreated)
            {
                int ex = GetWindowLong(Handle, -20);
                SetWindowLong(Handle, -20, locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
            }
            Invalidate();
        }
    }
    public void SetOpacity(int pct) { Opacity = Math.Max(0.2, Math.Min(1.0, pct / 100.0)); }

    // đòn mới (gọi trên luồng giao diện)
    public void Add(CombatHit h)
    {
        lines.Insert(0, new Line { H = h, Born = DateTime.Now });
        while (lines.Count > MaxLines) lines.RemoveAt(lines.Count - 1);
        if (Wanted && !Visible) Show();
        Invalidate();
    }

    // "Thử": vài đòn mẫu
    public void Demo()
    {
        Add(new CombatHit { Out = true, Name = "Henry", Species = "Allosaurus", Damage = 142, HpAfter = 640, Ability = "Bite", Cause = "ability" });
        Add(new CombatHit { Out = false, Name = "Henry", Species = "Allosaurus", Damage = 85, HpAfter = 1210, Ability = "Tail Whip", Cause = "ability" });
        Add(new CombatHit { Out = true, Name = "Henry", Species = "Allosaurus", Damage = 300, HpAfter = 0, Ability = "Bite", Cause = "ability", Killed = true });
    }

    void Tick()
    {
        var now = DateTime.Now;
        bool changed = false;
        for (int i = lines.Count - 1; i >= 0; i--)
            if ((now - lines[i].Born).TotalSeconds > LifeSec) { lines.RemoveAt(i); changed = true; }
        bool wantSample = Wanted && Active && !locked && lines.Count == 0;
        if (wantSample != sample) { sample = wantSample; changed = true; }
        bool show = Wanted && (lines.Count > 0 || (Active && !locked));
        if (!show) { if (Visible) Hide(); return; }
        if (!Visible) Show();
        if (lines.Count > 0) changed = true; // đang mờ dần -> vẽ lại mỗi 50 ms
        if (changed) Invalidate();
        if ((now - lastTopCheck).TotalMilliseconds > 1000)
        {
            lastTopCheck = now;
            if ((GetWindowLong(Handle, -20) & WS_EX_TOPMOST) == 0)
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        if (posDirty && Control.MouseButtons == MouseButtons.None)
        {
            posDirty = false;
            saveSetting("feedpos", Left + "," + Top + "," + scale.ToString("0.0#", CultureInfo.InvariantCulture));
        }
    }

    static Color Mix(Color a, Color b, double t) // t = 0 -> a, 1 -> b
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static string Pretty(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        foreach (var pre in new[] { "AM_", "BP_", "GA_", "Ability_" })
            if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) s = s.Substring(pre.Length);
        return s.Replace('_', ' ').Trim();
    }

    // "Henry · Allosaurus"
    static string WhoText(CombatHit h)
    {
        string n = string.IsNullOrEmpty(h.Name) ? "?" : h.Name;
        return string.IsNullOrEmpty(h.Species) ? n : n + " (" + h.Species + ")";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Key);
        int w = ClientSize.Width, y = S(2);
        if (sample)
        {
            using (var b = new SolidBrush(Color.FromArgb(24, 26, 20))) g.FillRectangle(b, 0, 0, w, ClientSize.Height);
            using (var pen = new Pen(Color.FromArgb(224, 184, 74)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawRectangle(pen, 0, 0, w - 1, ClientSize.Height - 1);
            TextRenderer.DrawText(g, "Thông báo từng đòn – kéo để đặt vị trí · lăn chuột = cỡ", fText, new Point(S(8), S(6)), Color.FromArgb(224, 184, 74), TextFormatFlags.NoPadding);
            return;
        }
        var now = DateTime.Now;
        var panel = Color.FromArgb(24, 26, 20);
        foreach (var ln in lines)
        {
            double age = (now - ln.Born).TotalSeconds;
            double fade = age <= LifeSec - FadeSec ? 0 : Math.Min(1, (age - (LifeSec - FadeSec)) / FadeSec); // 0 = rõ, 1 = biến mất
            var h = ln.H;
            Color accent = h.Out ? Color.FromArgb(120, 230, 110) : Color.FromArgb(255, 105, 95);
            Color txt = Color.FromArgb(235, 236, 228);
            Color bg = Mix(panel, Key, fade), ac = Mix(accent, Key, fade), tx = Mix(txt, Key, fade);
            var rc = new Rectangle(0, y, w, LineH - S(3));
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, rc);
            using (var b = new SolidBrush(ac)) g.FillRectangle(b, rc.X, rc.Y, S(4), rc.Height);

            string dmg = double.IsNaN(h.Damage) ? "?" : "−" + Math.Round(h.Damage);
            string who = WhoText(h);
            string skill = Pretty(h.Ability);
            if (skill.Length == 0) skill = Pretty(h.Cause);
            string tail = (skill.Length > 0 ? skill : "")
                + (h.Killed ? (skill.Length > 0 ? " · " : "") + (h.Out ? "hạ gục" : "bạn chết") : "");
            string head = h.Out ? "Bạn → " + who : who + " → Bạn";

            int x = S(10), ty = rc.Y + (rc.Height - fText.Height) / 2;
            string[] parts = { dmg + "  ", head + "   ", tail };
            Font[] fonts = { fBold, fText, fText };
            Color[] cols = { ac, tx, Mix(Color.FromArgb(170, 176, 150), Key, fade) };
            for (int i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0) continue;
                var sz = TextRenderer.MeasureText(g, parts[i], fonts[i], Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                TextRenderer.DrawText(g, parts[i], fonts[i], new Point(x, ty), cols[i], bg, TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                x += sz.Width;
            }
            y += LineH;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (locked || e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }
    protected override void OnMove(EventArgs e) { base.OnMove(e); posDirty = true; }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (locked) return;
        float ns = Math.Max(0.6f, Math.Min(2.5f, scale + (e.Delta > 0 ? 0.1f : -0.1f)));
        if (Math.Abs(ns - scale) < 0.001f) return;
        scale = ns; MakeFonts(); ClientSize = new Size(S(400), LineH * MaxLines + S(4));
        Invalidate(); posDirty = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); if (fText != null) { fText.Dispose(); fBold.Dispose(); } }
        base.Dispose(disposing);
    }
}
