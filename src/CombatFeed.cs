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
    public double LifeSec = 4.0;
    const double FadeSec = 0.8;          // đoạn mờ dần ở cuối đời dòng
    const double FlashSec = 0.35;        // nháy sáng khi dòng vừa xuất hiện (gây chú ý)
    const int MaxLines = 10;             // quá 10 dòng -> bỏ dòng cũ nhất
    const double CombatGapSec = 10;      // cách đòn trước > 10 s = trận mới -> đánh số lại từ #1
    bool locked = true, posDirty;
    float scale = 1f;
    Font fText, fBold, fBig, fName;
    DateTime lastTopCheck;
    static readonly Color Key = Color.FromArgb(1, 2, 3);

    class Line { public CombatHit H; public DateTime Born; public int No; }
    int seq; DateTime lastHitAt = DateTime.MinValue; // số thứ tự đòn kể từ lúc bắt đầu combat
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
        ClientSize = new Size(S(480), LineH * MaxLines + S(4));
        if (ok) Location = new Point(px, py);
        else { var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Left + (wa.Width - Width) / 2 + S(150), wa.Top + (int)(wa.Height * 0.55)); }

        timer = new Timer { Interval = 50 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }

    int LineH { get { return S(40); } }
    int S(float px) { return (int)Math.Round(px * scale); }
    void MakeFonts()
    {
        if (fText != null) { fText.Dispose(); fBold.Dispose(); fBig.Dispose(); fName.Dispose(); }
        fText = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
        fBold = new Font("Segoe UI", 9f * scale, FontStyle.Bold, GraphicsUnit.Point);
        fBig = new Font("Segoe UI", 18f * scale, FontStyle.Bold, GraphicsUnit.Point);   // số sát thương
        fName = new Font("Segoe UI", 11f * scale, FontStyle.Bold, GraphicsUnit.Point);  // ai → ai
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
        // số thứ tự từ lúc bắt đầu combat: đòn cách đòn trước > CombatGapSec thì là trận mới
        DateTime at = h.At == DateTime.MinValue ? DateTime.Now : h.At;
        if (lastHitAt == DateTime.MinValue || (at - lastHitAt).TotalSeconds > CombatGapSec) seq = 0;
        if (at > lastHitAt) lastHitAt = at;
        seq++;
        lines.Insert(0, new Line { H = h, Born = DateTime.Now, No = seq });
        while (lines.Count > MaxLines) lines.RemoveAt(lines.Count - 1);
        if (Wanted && !Visible) Show();
        Invalidate();
    }

    // "Thử": vài đòn mẫu
    public void Demo()
    {
        seq = 0; lastHitAt = DateTime.MinValue;
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
        var panel = Color.FromArgb(18, 20, 16);
        foreach (var ln in lines)
        {
            double age = (now - ln.Born).TotalSeconds;
            double fade = age <= LifeSec - FadeSec ? 0 : Math.Min(1, (age - (LifeSec - FadeSec)) / FadeSec); // 0 = rõ, 1 = biến mất
            double flash = age < FlashSec ? 1 - age / FlashSec : 0;                                          // 1 = vừa xuất hiện
            var h = ln.H;
            // bạn gây sát thương = xanh lá; bạn bị đánh = đỏ (nền ngả màu theo hướng đòn để nhìn là biết ngay)
            Color accent = h.Out ? Color.FromArgb(110, 240, 110) : Color.FromArgb(255, 90, 80);
            Color tint = h.Out ? (h.Killed ? Color.FromArgb(28, 96, 36) : Color.FromArgb(24, 66, 30)) : (h.Killed ? Color.FromArgb(120, 20, 20) : Color.FromArgb(88, 24, 22));
            Color bgc = Mix(Mix(panel, tint, 1.0), Color.White, flash * 0.30);
            Color txt = Color.FromArgb(245, 246, 238), sub = Color.FromArgb(200, 205, 185);
            Color bg = Mix(bgc, Key, fade), ac = Mix(accent, Key, fade), tx = Mix(txt, Key, fade), sb2 = Mix(sub, Key, fade);
            var rc = new Rectangle(0, y, w, LineH - S(4));
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, rc);
            using (var b = new SolidBrush(ac)) g.FillRectangle(b, rc.X, rc.Y, S(6), rc.Height);                 // vạch màu bên trái
            using (var pen = new Pen(Mix(ac, bg, 0.45))) g.DrawRectangle(pen, rc.X, rc.Y, rc.Width - 1, rc.Height - 1); // viền mảnh

            // 1) số sát thương rất to
            string dmg = double.IsNaN(h.Damage) ? "?" : "−" + Math.Round(h.Damage);
            var fl = TextFormatFlags.NoPadding | TextFormatFlags.NoClipping;
            var dsz = TextRenderer.MeasureText(g, dmg, fBig, Size.Empty, fl);
            string no = "#" + ln.No;
            var nsz = TextRenderer.MeasureText(g, no, fBold, Size.Empty, fl);
            TextRenderer.DrawText(g, no, fBold, new Point(S(12), rc.Y + (rc.Height - nsz.Height) / 2), sb2, bg, fl);
            int col1 = S(52), colW = S(82);
            TextRenderer.DrawText(g, dmg, fBig, new Point(col1 + Math.Max(0, colW - dsz.Width), rc.Y + (rc.Height - dsz.Height) / 2), ac, bg, fl);

            // 2) dòng trên: "Bạn ▶ Henry (Allosaurus)", dòng dưới: kỹ năng · máu còn lại
            string who = WhoText(h);
            string left = h.Out ? "Bạn" : who, right = h.Out ? who : "Bạn";
            int x = col1 + colW + S(12), ty = rc.Y + S(4);
            string[] seg = { left + " ", "▶ ", right };
            Color[] sc = { tx, ac, tx };
            for (int i = 0; i < 3; i++)
            {
                var sz = TextRenderer.MeasureText(g, seg[i], fName, Size.Empty, fl);
                TextRenderer.DrawText(g, seg[i], fName, new Point(x, ty), sc[i], bg, fl);
                x += sz.Width;
            }
            string skill = Pretty(h.Ability);
            if (skill.Length == 0) skill = Pretty(h.Cause);
            string hp = "";
            if (!double.IsNaN(h.HpAfter)) hp = (h.Out ? "máu nó còn " : "máu bạn còn ") + Math.Round(h.HpAfter);
            string line2 = skill.Length > 0 && hp.Length > 0 ? skill + "  ·  " + hp : skill + hp;
            if (line2.Length > 0)
                TextRenderer.DrawText(g, line2, fText, new Point(col1 + colW + S(12), ty + fName.Height + S(1)), sb2, bg, fl);

            // 3) nhãn kết quả ở mép phải
            if (h.Killed)
            {
                string tag = h.Out ? "HẠ GỤC" : "BẠN CHẾT";
                var tsz = TextRenderer.MeasureText(g, tag, fBold, Size.Empty, fl);
                var tr = new Rectangle(w - tsz.Width - S(22), rc.Y + (rc.Height - tsz.Height - S(6)) / 2, tsz.Width + S(14), tsz.Height + S(6));
                Color tbg = Mix(h.Out ? Color.FromArgb(120, 240, 110) : Color.FromArgb(230, 60, 55), Key, fade);
                Color tfg = Mix(h.Out ? Color.FromArgb(10, 40, 10) : Color.White, Key, fade);
                using (var b = new SolidBrush(tbg)) g.FillRectangle(b, tr);
                TextRenderer.DrawText(g, tag, fBold, new Point(tr.X + S(7), tr.Y + S(3)), tfg, tbg, fl);
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
        scale = ns; MakeFonts(); ClientSize = new Size(S(480), LineH * MaxLines + S(4));
        Invalidate(); posDirty = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); if (fText != null) { fText.Dispose(); fBold.Dispose(); fBig.Dispose(); fName.Dispose(); } }
        base.Dispose(disposing);
    }
}
