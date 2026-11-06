using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Overlay chỉ số khủng long của bạn (từ PvnLive): cửa sổ WinForms nhỏ tự vẽ (GDI), KHÔNG dùng trình duyệt.
//  - Luôn nổi trên game, không lấy focus. Khoá (chung Ctrl+F9 với radar): bấm xuyên.
//  - Mở khoá: kéo để di chuyển, lăn chuột để đổi cỡ. Vị trí + cỡ lưu ở settings.txt (vitalspos=x,y,cỡ).
//  - Chỉ vẽ lại khi có frame mới (~4 lần/s) hoặc khi chuyển sang "mất tín hiệu" -> gần như không tốn gì.
class VitalsOverlay : Form
{
    readonly PvnLive src;
    readonly PvnCombat combat;
    readonly Func<string, string, string> loadSetting; readonly Action<string, string> saveSetting;
    readonly Timer timer;
    public bool Wanted;          // tool muốn hiện (ô "Kết nối PVN" + ô "Hiện overlay chỉ số")
    bool locked = true;
    float scale = 1f;
    Vitals cur; bool curStale;
    readonly List<KeyValuePair<DateTime, double>> bloodHist = new List<KeyValuePair<DateTime, double>>();
    bool posDirty; DateTime lastTopCheck;
    Font fText, fBold;

    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public VitalsOverlay(PvnLive src, PvnCombat combat, Func<string, string, string> load, Action<string, string> save)
    {
        this.src = src; this.combat = combat; loadSetting = load; saveSetting = save;
        Text = "Dino Vitals Overlay";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 22, 17);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Opacity = 0.85;

        // vị trí/cỡ đã lưu; mặc định: mép trái, hơi dưới giữa màn hình chính
        var wa = Screen.PrimaryScreen.WorkingArea;
        int x = wa.Left + 20, y = wa.Top + wa.Height * 55 / 100;
        string[] p = (loadSetting("vitalspos", "") ?? "").Split(',');
        int px, py; float ps;
        if (p.Length == 3 && int.TryParse(p[0], out px) && int.TryParse(p[1], out py) &&
            float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ps))
        {
            bool on = false;
            foreach (var s in Screen.AllScreens) if (s.WorkingArea.Contains(px + 20, py + 10)) on = true;
            if (on) { x = px; y = py; scale = Math.Max(0.6f, Math.Min(2.5f, ps)); }
        }
        Location = new Point(x, y);
        MakeFonts();
        Relayout();

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
                ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
                SetWindowLong(Handle, -20, ex);
            }
            Relayout(); Invalidate();
        }
    }

    public void SetOpacity(int pct) { Opacity = Math.Max(0.2, Math.Min(1.0, pct / 100.0)); }

    void MakeFonts()
    {
        if (fText != null) { fText.Dispose(); fBold.Dispose(); }
        fText = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
        fBold = new Font("Segoe UI", 9f * scale, FontStyle.Bold, GraphicsUnit.Point);
    }

    void Tick()
    {
        var v = src.Last;
        bool show = Wanted && v != null;
        if (!show) { if (Visible) Hide(); cur = null; bloodHist.Clear(); return; }

        bool stale = (DateTime.Now - v.At).TotalSeconds > 5;
        if (!ReferenceEquals(v, cur))
        {
            cur = v;
            if (v.HasDino)
            {
                if (bloodHist.Count > 0 && v.Blood > bloodHist[bloodHist.Count - 1].Value + 0.5) bloodHist.Clear(); // hồi máu / đổi khủng long
                bloodHist.Add(new KeyValuePair<DateTime, double>(v.At, v.Blood));
                while (bloodHist.Count > 0 && (v.At - bloodHist[0].Key).TotalSeconds > 10) bloodHist.RemoveAt(0);
            }
            else bloodHist.Clear();
            curStale = stale;
            Relayout(); Invalidate();
        }
        else if (stale != curStale) { curStale = stale; Relayout(); Invalidate(); }

        // combat: đối thủ đang đánh nhau với bạn (đổi mỗi 2 s) -> vẽ lại khi danh sách / máu đổi
        string sig = CombatSig();
        if (sig != foeSig) { foeSig = sig; Relayout(); Invalidate(); }
        // nhiệm vụ Prime (đổi mỗi ~10 s khi PVN trả /me mới)
        string psig = PrimeSig();
        if (psig != primeSig) { primeSig = psig; Relayout(); Invalidate(); }

        if (!Visible) Show();
        // giữ nổi trên game: chỉ gọi SetWindowPos khi mất cờ TOPMOST (gọi liên tục làm khựng)
        if ((DateTime.Now - lastTopCheck).TotalMilliseconds > 1000)
        {
            lastTopCheck = DateTime.Now;
            if ((GetWindowLong(Handle, -20) & WS_EX_TOPMOST) == 0)
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
        }
        if (posDirty && Control.MouseButtons == MouseButtons.None) { posDirty = false; SavePos(); }
    }

    void SavePos()
    {
        saveSetting("vitalspos", Left + "," + Top + "," + scale.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture));
    }

    public Func<bool> FoesOn; // ô "hiện máu đối thủ" (độc lập với thông báo từng đòn)
    CombatFoe[] foeList = new CombatFoe[0]; string foeSig = "";
    string CombatSig()
    {
        if (combat == null || (FoesOn != null && !FoesOn())) return "";
        var f = combat.Foes; var sb = new StringBuilder();
        foreach (var x in f) sb.Append(x.SteamId).Append(':').Append((int)x.Hp).Append(x.Killed ? "k" : "").Append('|');
        return sb.ToString();
    }

    // Nhiệm vụ Prime của khủng long đang dùng (PvnPrime): chưa đủ -> liệt kê nhiệm vụ chưa xong; đủ -> chỉ ghi chú đã hoàn thành
    public PvnPrime Prime; public Func<bool> PrimeOn;
    string primeSig = "";
    PrimeInfo CurPrime() { return Prime != null && (PrimeOn == null || PrimeOn()) ? Prime.Last : null; }
    string PrimeSig()
    {
        var p = CurPrime(); if (p == null) return "";
        var sb = new StringBuilder(); sb.Append(p.Done).Append('/').Append(p.Required).Append(p.Complete ? "c" : "n");
        foreach (var q in p.Quests) sb.Append(q.Done ? '1' : '0');
        return sb.ToString();
    }
    string primeHead, primeNote; readonly List<string> primeQuests = new List<string>();

    // ---- nội dung ----
    struct Row { public string Label, Num; public double Pct; public Color Col; public bool Warn; }
    readonly List<Row> rows = new List<Row>();
    readonly List<string> status = new List<string>();
    string header, footer;

    double BloodRate() // blood mất mỗi giây (> 0 = đang tụt), đo trên ~10 s
    {
        if (bloodHist.Count < 2) return 0;
        var a = bloodHist[0]; var b = bloodHist[bloodHist.Count - 1];
        double dt = (b.Key - a.Key).TotalSeconds;
        return dt >= 2 ? (a.Value - b.Value) / dt : 0;
    }

    static string Mss(double s) { int t = (int)Math.Max(0, s); return (t / 60) + ":" + (t % 60).ToString("00"); }

    void AddRow(string lab, double v, double max, Color col, bool always)
    {
        if (!(max > 0)) return;
        double p = Math.Max(0, Math.Min(100, v / max * 100));
        if (!always && p >= 99.5) return;
        string num = numbers ? Fmt(v) + "/" + Fmt(max) : Math.Round(p) + "%";
        rows.Add(new Row { Label = lab, Num = num, Pct = p, Col = col, Warn = p < 25 });
    }

    // số thật: max ≥ 100 -> số nguyên (khát 700/1000), nhỏ hơn -> 1 chữ số lẻ (đói 4.1/16.5)
    static string Fmt(double x)
    {
        return Math.Abs(x) >= 100 ? Math.Round(x).ToString() : Math.Round(x, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Hiện số thật (31/50) thay vì % (ô trong tool, settings.txt vitalsnum)
    bool numbers;
    public bool ShowNumbers { get { return numbers; } set { if (numbers == value) return; numbers = value; Relayout(); Invalidate(); } }

    int S(float px) { return (int)Math.Round(px * scale); }

    void Relayout()
    {
        rows.Clear(); status.Clear(); header = null; footer = null; primeHead = null; primeNote = null; primeQuests.Clear();
        var v = cur;
        if (v != null && !v.HasDino) header = "PVN: chưa có khủng long (đang ở menu?)";
        else if (v != null)
        {
            bool bleeding = v.Bleeding;
            AddRow("Máu", v.Health, v.MaxHealth, Color.FromArgb(255, 81, 72), true);
            AddRow("Blood", v.Blood, v.MaxBlood, Color.FromArgb(200, 40, 52), bleeding);
            AddRow("Thể lực", v.Stamina, v.MaxStamina, Color.FromArgb(240, 192, 64), true);
            AddRow("Đói", v.Hunger, v.MaxHunger, Color.FromArgb(224, 138, 60), true);
            AddRow("Khát", v.Thirst, v.MaxThirst, Color.FromArgb(79, 179, 255), true);
            AddRow("Oxy", v.Oxygen, v.MaxOxygen, Color.FromArgb(127, 224, 255), false);
            if (bleeding)
            {
                double r = BloodRate();
                status.Add("Chảy máu" + (r > 0.01 ? " −" + r.ToString("0.00") + "/s" + (v.Blood > 0 ? " (hết sau ~" + Mss(v.Blood / r) + ")" : "") : ""));
            }
            foreach (var d in v.Debuffs) if (d != "bleeding") status.Add(d);
            // gãy xương: 3 chỗ (chân, thân, đầu), mỗi chỗ đang gãy / chưa hồi xong = 1 thanh tiến độ hồi phục (luôn là %)
            string[] bn = { "Gãy chân", "Gãy thân", "Gãy đầu" };
            for (int i = 0; i < 3; i++)
                if (v.Fractured[i] || v.RecoverPct[i] < 99.5)
                {
                    double rp = Math.Max(0, Math.Min(100, v.RecoverPct[i]));
                    rows.Add(new Row { Label = v.Fractured[i] ? bn[i] : "Xương " + bn[i].Substring(4), Num = Math.Round(rp) + "%", Pct = rp, Col = Color.FromArgb(200, 196, 180), Warn = v.Fractured[i] });
                }
            if (v.GlassBonesSec > 0) status.Add("Xương giòn " + Mss(v.GlassBonesSec));
            if (v.Sick) status.Add("Ốm");
            if (v.Bacteria > 0) status.Add("Vi khuẩn " + Math.Round(v.Bacteria));
        }
        var pr = v != null && v.HasDino ? CurPrime() : null;
        if (pr != null)
        {
            if (pr.Complete) primeNote = "✔ Đã hoàn thành Prime";
            else
            {
                primeHead = "★ Nhiệm vụ Prime " + pr.Done + "/" + pr.Total + (pr.Required > 0 ? " (cần " + pr.Required + ")" : "");
                foreach (var q in pr.Quests) if (!q.Done) primeQuests.Add(PvnPrime.Viet(q.Name));
            }
        }
        foeList = combat != null && (FoesOn == null || FoesOn()) ? combat.Foes : new CombatFoe[0];

        if (curStale) footer = "PVN: mất tín hiệu";
        if (!locked) footer = (footer != null ? footer + " · " : "") + "kéo để di chuyển · lăn chuột = cỡ";

        // cỡ cửa sổ
        int w = S(numbers ? 246 : 206), pad = S(6), rowH = S(17), h = pad;
        if (header != null) h += S(17);
        h += rows.Count * rowH;
        if (status.Count > 0) h += Measure(string.Join(" · ", status), fBold, w - 2 * pad) + S(2);
        if (primeHead != null) h += S(8) + S(17) + primeQuests.Count * S(15);
        else if (primeNote != null) h += S(8) + S(16);
        if (foeList.Length > 0) h += S(8) + S(17) + Math.Min(foeList.Length, 5) * (S(15) + rowH);
        if (footer != null) h += Measure(footer, fText, w - 2 * pad);
        h += pad;
        if (ClientSize.Width != w || ClientSize.Height != h) ClientSize = new Size(w, h);
    }

    static int Measure(string s, Font f, int width)
    {
        return TextRenderer.MeasureText(s, f, new Size(width, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = ClientSize.Width, pad = S(6), rowH = S(17), y = pad;
        Color muted = Color.FromArgb(154, 160, 140), text = Color.FromArgb(228, 230, 220), red = Color.FromArgb(255, 138, 128);
        if (curStale) { muted = Color.FromArgb(100, 104, 92); text = muted; red = Color.FromArgb(150, 100, 96); }
        using (var pen = new Pen(locked ? Color.FromArgb(46, 51, 39) : Color.FromArgb(224, 184, 74)))
            g.DrawRectangle(pen, 0, 0, w - 1, ClientSize.Height - 1);

        if (header != null) { TextRenderer.DrawText(g, header, fText, new Point(pad, y), muted, TextFormatFlags.NoPadding); y += S(17); }
        int labW = S(64), numW = S(numbers ? 70 : 34), barX = pad + labW, barW = w - pad - numW - barX - S(4), barH = Math.Max(4, S(7));
        using (var track = new SolidBrush(Color.FromArgb(42, 47, 34)))
            foreach (var r in rows)
            {
                TextRenderer.DrawText(g, r.Label, fText, new Rectangle(pad, y, labW, rowH), muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                int by = y + (rowH - barH) / 2;
                g.FillRectangle(track, barX, by, barW, barH);
                Color c = curStale ? Color.FromArgb(c2(r.Col.R), c2(r.Col.G), c2(r.Col.B)) : r.Col;
                using (var b = new SolidBrush(c)) g.FillRectangle(b, barX, by, (int)Math.Round(barW * r.Pct / 100), barH);
                TextRenderer.DrawText(g, r.Num, fText, new Rectangle(w - pad - numW, y, numW, rowH), r.Warn ? red : text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                y += rowH;
            }
        if (status.Count > 0)
        {
            string s = string.Join(" · ", status);
            int h = Measure(s, fBold, w - 2 * pad);
            y += S(2);
            TextRenderer.DrawText(g, s, fBold, new Rectangle(pad, y, w - 2 * pad, h), red, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += h;
        }
        // ---- Prime: chưa đủ -> nhiệm vụ chưa xong; đủ -> ghi chú ----
        if (primeHead != null || primeNote != null)
        {
            y += S(8);
            using (var sep = new Pen(Color.FromArgb(60, 66, 50))) g.DrawLine(sep, pad, y - S(4), w - pad, y - S(4));
            if (primeHead != null)
            {
                TextRenderer.DrawText(g, primeHead, fBold, new Point(pad, y), curStale ? muted : Color.FromArgb(240, 200, 90), TextFormatFlags.NoPadding);
                y += S(17);
                foreach (var qn in primeQuests)
                {
                    TextRenderer.DrawText(g, "✘ " + qn, fText, new Rectangle(pad, y, w - 2 * pad, S(15)), curStale ? muted : red, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    y += S(15);
                }
            }
            else
            {
                TextRenderer.DrawText(g, primeNote, fText, new Point(pad, y), curStale ? muted : Color.FromArgb(110, 231, 160), TextFormatFlags.NoPadding);
                y += S(16);
            }
        }
        // ---- combat: máu đối thủ đang đánh nhau với bạn ----
        if (foeList.Length > 0)
        {
            y += S(8);
            using (var sep = new Pen(Color.FromArgb(60, 66, 50))) g.DrawLine(sep, pad, y - S(4), w - pad, y - S(4));
            TextRenderer.DrawText(g, "⚔ Đối thủ (" + foeList.Length + ")", fBold, new Point(pad, y), Color.FromArgb(255, 170, 120), TextFormatFlags.NoPadding);
            y += S(17);
            Color foeHp = curStale ? Color.FromArgb(150, 70, 66) : Color.FromArgb(230, 90, 80);
            var ell = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            using (var track = new SolidBrush(Color.FromArgb(42, 47, 34)))
                for (int i = 0; i < foeList.Length && i < 5; i++)
                {
                    var f = foeList[i];
                    // dòng tên: loài · tên · % trưởng thành
                    var parts = new List<string>();
                    parts.Add(string.IsNullOrEmpty(f.Species) ? "dino" : f.Species);
                    if (!string.IsNullOrEmpty(f.Name)) parts.Add(f.Name);
                    if (f.Growth >= 0) parts.Add(Math.Round(f.Growth * 100) + "%");
                    TextRenderer.DrawText(g, string.Join(" · ", parts.ToArray()), fText, new Rectangle(pad, y, w - 2 * pad, S(15)), text, TextFormatFlags.VerticalCenter | ell);
                    y += S(15);
                    // dòng máu đối thủ: thanh chạy hết bề ngang (nếu đã biết máu) + số bên phải
                    int fBarW = w - 2 * pad - numW - S(4);
                    int by = y + (rowH - barH) / 2;
                    g.FillRectangle(track, pad, by, fBarW, barH);
                    string num; double pct;
                    if (f.Killed) { num = "hạ gục"; pct = 0; }
                    else if (f.Hp >= 0) { num = Math.Round(f.Hp).ToString(); pct = f.MaxSeen > 0 ? Math.Max(0, Math.Min(100, f.Hp / f.MaxSeen * 100)) : 100; }
                    else { num = "máu ?"; pct = 0; }
                    if (pct > 0) using (var b = new SolidBrush(foeHp)) g.FillRectangle(b, pad, by, (int)Math.Round(fBarW * pct / 100), barH);
                    TextRenderer.DrawText(g, num, fText, new Rectangle(w - pad - numW, y, numW, rowH), f.Killed || (f.Hp >= 0 && pct < 25) ? red : text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    y += rowH;
                }
        }
        if (footer != null)
            TextRenderer.DrawText(g, footer, fText, new Rectangle(pad, y, w - 2 * pad, Measure(footer, fText, w - 2 * pad)), muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    static int c2(int x) { return x / 2 + 20; } // làm tối khi mất tín hiệu

    // ---- mở khoá: kéo + lăn chuột ----
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (locked || e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, new IntPtr(2 /*HTCAPTION*/), IntPtr.Zero);
    }

    protected override void OnMove(EventArgs e) { base.OnMove(e); posDirty = true; }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (locked) return;
        float ns = Math.Max(0.6f, Math.Min(2.5f, scale + (e.Delta > 0 ? 0.1f : -0.1f)));
        if (Math.Abs(ns - scale) < 0.001f) return;
        scale = ns; MakeFonts(); Relayout(); Invalidate(); posDirty = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); if (fText != null) { fText.Dispose(); fBold.Dispose(); } }
        base.Dispose(disposing);
    }
}
