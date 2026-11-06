using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Thanh la bàn (giống game bắn súng): giữa = hướng CAMERA của bạn (RemoteViewYaw trong replay),
// khủng long người chơi xung quanh là chấm trên thanh; ngoài tầm thanh -> mũi tên ◀ ▶ ở mép.
// Quay chuột tới khi chấm vào giữa = đang nhìn thẳng về con đó.
class CompassTarget
{
    public string Name, Species; public double Growth = -1, Dist, Bearing; // Bearing: 0 = Bắc, 90 = Đông
}
class CompassData
{
    public double View;          // hướng camera theo la bàn (0 = Bắc)
    public bool ViewIsBody;      // không có hướng camera -> dùng hướng thân
    public List<CompassTarget> Targets = new List<CompassTarget>();
    public DateTime At;
}

partial class Replay
{
    // Gọi trên luồng đọc replay (sau ParseMore). null = chưa biết bạn ở đâu / hướng nào.
    public CompassData CompassSnapshot(double maxDistM)
    {
        Actor me = FindMe();
        if (me == null || me.Track.Count == 0) return null;
        double[] mp = me.Track[me.Track.Count - 1];
        var cd = new CompassData { At = DateTime.Now };
        double vy;
        // RemoteViewYaw = 1 byte (0..255 -> 0..360°), yaw Unreal (0 = Đông). Kiểm chứng fast7: khi chạy thẳng trùng hướng thân (lệch 0 ± 15°)
        if (double.TryParse(me.Get("RemoteViewYaw"), NumberStyles.Float, IC, out vy)) cd.View = Norm(YawToCompass(vy * 360.0 / 256));
        else
        {
            double[] wy = null;
            for (int i = me.Track.Count - 1; i >= 0 && wy == null; i--) if (me.Track[i].Length > 4) wy = me.Track[i];
            if (wy == null) return null;
            cd.View = Norm(YawToCompass(wy[4])); cd.ViewIsBody = true;
        }
        Func<Actor, string> owner = null;
        foreach (var a in dinoActors)
        {
            if (a == me || a.CloseTime >= 0 || a.Track.Count == 0) continue;
            if (Unq(a.Get("SteamId")) == "" || a.Get("Controller") != "" || a.Get("bIsDead") == "1") continue;
            double[] p = a.Track[a.Track.Count - 1];
            double d = Dist(mp, p);
            if (d > maxDistM) continue;
            if (owner == null) owner = MakeOwner(PlayerStates());
            double g;
            cd.Targets.Add(new CompassTarget
            {
                Name = owner(a), Species = Species(a), Dist = d, Bearing = Norm(Bearing(mp, p)),
                Growth = double.TryParse(a.Get("Growth"), NumberStyles.Float, IC, out g) ? g : -1
            });
        }
        cd.Targets.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        return cd;
    }
    static double Norm(double deg) { return ((deg % 360) + 360) % 360; }
}

class CompassOverlay : Form
{
    readonly Func<string, string, string> loadSetting; readonly Action<string, string> saveSetting;
    readonly Timer timer;
    public bool Wanted;
    bool locked = true, posDirty, stale;
    float scale = 1f;
    CompassData data;
    DateTime lastTopCheck;
    Font fText, fSmall, fBold;
    const double Half = 90;        // thanh hiện ±90° quanh hướng nhìn
    public double Fov = 90;        // khung "mép màn hình" (FOV ngang của game, ước lượng)

    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public CompassOverlay(Func<string, string, string> load, Action<string, string> save)
    {
        loadSetting = load; saveSetting = save;
        Text = "Dino Compass Overlay";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 22, 17);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Opacity = 0.85;
        double fov;
        if (double.TryParse(loadSetting("compassfov", "90"), NumberStyles.Float, CultureInfo.InvariantCulture, out fov) && fov >= 30 && fov <= 170) Fov = fov;

        string[] p = (loadSetting("compasspos", "") ?? "").Split(',');
        int px = 0, py = 0; float ps; bool ok = false;
        if (p.Length == 3 && int.TryParse(p[0], out px) && int.TryParse(p[1], out py) &&
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ps))
        {
            foreach (var s in Screen.AllScreens) if (s.WorkingArea.Contains(px + 20, py + 10)) ok = true;
            if (ok) scale = Math.Max(0.6f, Math.Min(2.5f, ps));
        }
        MakeFonts();
        ClientSize = new Size(S(560), S(84));
        if (ok) Location = new Point(px, py);
        else { var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 6); }

        timer = new Timer { Interval = 250 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
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

    // dữ liệu mới từ luồng đọc replay (gọi trên luồng giao diện)
    public void SetData(CompassData d)
    {
        if (d == null) return;
        data = d; stale = false;
        if (Wanted && !Visible) Show();
        Invalidate();
    }

    void MakeFonts()
    {
        if (fText != null) { fText.Dispose(); fSmall.Dispose(); fBold.Dispose(); }
        fText = new Font("Segoe UI", 8.5f * scale, FontStyle.Regular, GraphicsUnit.Point);
        fSmall = new Font("Segoe UI", 7.5f * scale, FontStyle.Regular, GraphicsUnit.Point);
        fBold = new Font("Segoe UI", 9f * scale, FontStyle.Bold, GraphicsUnit.Point);
    }
    int S(float px) { return (int)Math.Round(px * scale); }

    void Tick()
    {
        double age = data == null ? 1e9 : (DateTime.Now - data.At).TotalSeconds;
        // không còn quay / tắt -> ẩn; > 3 s không có dữ liệu mới -> tối đi
        bool show = Wanted && age < 20;
        if (!show) { if (Visible) Hide(); return; }
        if (!Visible) Show();
        bool st = age > 3;
        if (st != stale) { stale = st; Invalidate(); }
        if ((DateTime.Now - lastTopCheck).TotalMilliseconds > 1000)
        {
            lastTopCheck = DateTime.Now;
            if ((GetWindowLong(Handle, -20) & WS_EX_TOPMOST) == 0)
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        if (posDirty && Control.MouseButtons == MouseButtons.None)
        {
            posDirty = false;
            saveSetting("compasspos", Left + "," + Top + "," + scale.ToString("0.0#", CultureInfo.InvariantCulture));
        }
    }

    static readonly string[] Card = { "B", "ĐB", "Đ", "ĐN", "N", "TN", "T", "TB" };
    static double Rel(double bearing, double view) { return ((bearing - view) % 360 + 540) % 360 - 180; } // -180..180, âm = bên trái

    Color DistColor(double d)
    {
        Color c = d < 50 ? Color.FromArgb(255, 90, 79) : d < 150 ? Color.FromArgb(255, 160, 70) : Color.FromArgb(230, 200, 110);
        return stale ? Color.FromArgb(c.R / 2 + 20, c.G / 2 + 20, c.B / 2 + 20) : c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width, h = ClientSize.Height, pad = S(8);
        Color muted = stale ? Color.FromArgb(90, 94, 84) : Color.FromArgb(154, 160, 140);
        Color text = stale ? Color.FromArgb(120, 124, 110) : Color.FromArgb(228, 230, 220);
        using (var pen = new Pen(locked ? Color.FromArgb(46, 51, 39) : Color.FromArgb(224, 184, 74)))
            g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
        var d = data;
        if (d == null) return;

        int x0 = pad + S(14), x1 = w - pad - S(14), cx = (x0 + x1) / 2;   // chừa chỗ cho mũi tên ở 2 mép
        double ppd = (x1 - x0) / (2 * Half);
        int stripY = S(4), stripH = S(22), base_ = stripY + stripH;

        // khung "trong màn hình" (±FOV/2)
        int fx0 = (int)(cx - Fov / 2 * ppd), fx1 = (int)(cx + Fov / 2 * ppd);
        using (var b = new SolidBrush(Color.FromArgb(stale ? 22 : 34, 255, 255, 255))) g.FillRectangle(b, fx0, stripY, fx1 - fx0, stripH);

        // vạch mỗi 15°, chữ hướng mỗi 45°
        using (var tick = new Pen(muted))
            for (int deg = 0; deg < 360; deg += 15)
            {
                double r = Rel(deg, d.View);
                if (Math.Abs(r) > Half) continue;
                int x = (int)Math.Round(cx + r * ppd);
                bool major = deg % 45 == 0;
                g.DrawLine(tick, x, base_ - (major ? S(6) : S(3)), x, base_);
                if (major)
                {
                    string lab = Card[deg / 45];
                    var sz = TextRenderer.MeasureText(lab, fBold, Size.Empty, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, lab, deg % 90 == 0 ? fBold : fSmall, new Point(x - sz.Width / 2, stripY), deg == 0 ? Color.FromArgb(255, 120, 110) : muted, TextFormatFlags.NoPadding);
                }
            }
        // tâm = hướng bạn nhìn
        using (var b = new SolidBrush(stale ? Color.FromArgb(120, 100, 40) : Color.FromArgb(255, 211, 77)))
            g.FillPolygon(b, new[] { new Point(cx - S(5), stripY - S(1)), new Point(cx + S(5), stripY - S(1)), new Point(cx, stripY + S(6)) });

        // khủng long: chấm trên thanh + nhãn bên dưới (tối đa 3 hàng, nhãn tự dồn sang bên / xuống hàng dưới để không đè nhau)
        int labY = base_ + S(4), labH = S(15);
        var used = new List<Rectangle>();
        var tgs = d.Targets;                     // đã sắp theo khoảng cách (gần trước = ưu tiên chỗ tốt nhất)
        int offL = 0, offR = 0;
        foreach (var t in tgs) { double r0 = Rel(t.Bearing, d.View); if (Math.Abs(r0) > Half) { if (r0 < 0) offL++; else offR++; } }

        // 1) chấm: vẽ con xa trước, con gần sau (đè lên). Chấm trùng hướng thì so le 2 mức cao thấp + viền tối để tách ra
        var dots = new List<int[]>();            // {x, mức}
        for (int i = tgs.Count - 1; i >= 0; i--)
        {
            var t = tgs[i];
            double r = Rel(t.Bearing, d.View);
            if (Math.Abs(r) > Half) continue;
            Color c = PvnFriends.IsFriend(t.Name) ? (stale ? Color.FromArgb(40, 110, 50) : Color.FromArgb(80, 220, 100)) : DistColor(t.Dist);
            int x = (int)Math.Round(cx + r * ppd), rad = S(t.Dist < 50 ? 5 : 4);
            int lvl = 0;
            for (int tryL = 0; tryL < 2; tryL++)
            {
                bool clash = false; foreach (var q in dots) if (q[1] == tryL && Math.Abs(q[0] - x) < rad * 2 + S(1)) clash = true;
                if (!clash) { lvl = tryL; break; }
                lvl = (tryL + 1) % 2;
            }
            dots.Add(new[] { x, lvl });
            int cy = base_ - (lvl == 0 ? S(12) : S(5));
            using (var b = new SolidBrush(c)) g.FillEllipse(b, x - rad, cy - rad, rad * 2, rad * 2);
            using (var pen = new Pen(Color.FromArgb(200, 12, 14, 10), Math.Max(1f, S(1)))) g.DrawEllipse(pen, x - rad, cy - rad, rad * 2, rad * 2);
        }

        // 2) nhãn theo thứ tự gần → xa
        int hidden = 0, doneL = 0, doneR = 0;
        for (int pass = 0; pass < 2; pass++)   // lượt 0: con ngoài thanh (nhãn mũi tên ở mép, cần luôn thấy), lượt 1: con trong thanh
        foreach (var t in tgs)
        {
            double r = Rel(t.Bearing, d.View);
            if ((Math.Abs(r) > Half) != (pass == 0)) continue;
            Color c = PvnFriends.IsFriend(t.Name) ? (stale ? Color.FromArgb(40, 110, 50) : Color.FromArgb(80, 220, 100)) : DistColor(t.Dist);
            string nm = string.IsNullOrEmpty(t.Name) ? t.Species : t.Name;
            if (nm != null && nm.Length > 16) nm = nm.Substring(0, 15) + "…";   // tên dài chiếm chỗ → cắt bớt
            string lab = nm + " " + Math.Round(t.Dist) + "m";
            if (Math.Abs(r) <= Half)
            {
                int x = (int)Math.Round(cx + r * ppd);
                var sz = TextRenderer.MeasureText(lab, fText, Size.Empty, TextFormatFlags.NoPadding);
                bool placed = false;
                for (int row = 0; row < 3 && !placed; row++)
                {
                    int y = labY + row * labH;
                    int ideal = Math.Max(1, Math.Min(w - sz.Width - 1, x - sz.Width / 2));
                    // thử giữa chấm, rồi dịch sang phải / trái qua nhãn đang chắn
                    var cands = new List<int> { ideal };
                    foreach (var u in used)
                        if (u.Y == y)
                        {
                            cands.Add(u.Right + S(6));
                            cands.Add(u.Left - sz.Width - S(6));
                        }
                    foreach (int cxl in cands)
                    {
                        if (cxl < 1 || cxl + sz.Width > w - 1) continue;
                        if (Math.Abs(cxl + sz.Width / 2 - x) > S(70)) continue;      // không dịch quá xa chấm của nó
                        var rc = new Rectangle(cxl, y, sz.Width, labH);
                        bool hit = false; foreach (var u in used) if (u.IntersectsWith(Rectangle.Inflate(rc, S(4), 0))) { hit = true; break; }
                        if (hit) continue;
                        used.Add(rc); placed = true;
                        TextRenderer.DrawText(g, lab, fText, rc, c, TextFormatFlags.NoPadding);
                        // nhãn bị dời hoặc ở hàng dưới -> vạch nối mảnh lên chấm cho biết của con nào
                        if (row > 0 || Math.Abs(cxl + sz.Width / 2 - x) > S(10))
                            using (var pen = new Pen(Color.FromArgb(110, c.R, c.G, c.B)))
                                g.DrawLine(pen, x, base_ - S(1), Math.Max(cxl + 2, Math.Min(cxl + sz.Width - 2, x)), y + S(1));
                        break;
                    }
                }
                if (!placed) hidden++;
            }
            else
            {
                // ngoài thanh: mũi tên ở mép bên cần quay sang; chỉ nhãn con gần nhất mỗi bên, kèm "+N" số con còn lại
                bool left = r < 0;
                if (left ? doneL++ > 0 : doneR++ > 0) continue;
                int ax = left ? pad : w - pad, ay = stripY + stripH / 2, aw = S(9);
                using (var b = new SolidBrush(c))
                    g.FillPolygon(b, left ? new[] { new Point(ax, ay), new Point(ax + aw, ay - S(7)), new Point(ax + aw, ay + S(7)) }
                                           : new[] { new Point(ax, ay), new Point(ax - aw, ay - S(7)), new Point(ax - aw, ay + S(7)) });
                int more = (left ? offL : offR) - 1;
                string s2 = lab + (Math.Abs(r) > 150 ? " (sau lưng)" : "") + (more > 0 ? "  +" + more : "");
                var sz = TextRenderer.MeasureText(s2, fText, Size.Empty, TextFormatFlags.NoPadding);
                bool placed = false;
                for (int row = 0; row < 3 && !placed; row++)
                {
                    var rc = new Rectangle(left ? pad : w - pad - sz.Width, labY + row * labH, sz.Width, labH);
                    bool hit = false; foreach (var u in used) if (u.IntersectsWith(Rectangle.Inflate(rc, S(4), 0))) { hit = true; break; }
                    if (hit) continue;
                    used.Add(rc); placed = true;
                    TextRenderer.DrawText(g, s2, fText, rc, c, TextFormatFlags.NoPadding);
                }
                if (!placed) hidden++;
            }
        }
        if (hidden > 0)
        {
            string hs = "+" + hidden + " ẩn";
            var hsz = TextRenderer.MeasureText(hs, fSmall, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, hs, fSmall, new Point(w - hsz.Width - pad, h - hsz.Height - S(2)), muted, TextFormatFlags.NoPadding);
        }

        string foot = stale ? "chưa có dữ liệu mới" : d.ViewIsBody ? "(hướng thân – không có hướng camera)" : null;
        if (!locked) foot = (foot != null ? foot + " · " : "") + "kéo để di chuyển · lăn chuột = cỡ";
        if (foot != null)
        {
            var sz = TextRenderer.MeasureText(foot, fSmall, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, foot, fSmall, new Point(cx - sz.Width / 2, h - sz.Height - S(2)), muted, TextFormatFlags.NoPadding);
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
        int cxOld = Left + Width / 2;
        scale = ns; MakeFonts(); ClientSize = new Size(S(560), S(84)); Left = cxOld - Width / 2;
        Invalidate(); posDirty = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); if (fText != null) { fText.Dispose(); fSmall.Dispose(); fBold.Dispose(); } }
        base.Dispose(disposing);
    }
}
