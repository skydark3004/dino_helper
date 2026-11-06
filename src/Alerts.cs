using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;

// Một thông báo "có khủng long người chơi mới vào vùng"
class DinoAlert
{
    public string Title, Detail; public double ReplayTime;
    public bool Garage; // "lấy dino ra từ gara" (ô cam + âm riêng), khác "vào vùng"
}

partial class Replay
{
    static readonly string[] RadarDir = { "phía trên", "trên-phải", "bên phải", "dưới-phải", "phía dưới", "dưới-trái", "bên trái", "trên-trái" };

    // Khủng long người chơi (có SteamId, không phải bạn, còn sống) đang ở trong vùng (kênh còn mở).
    // lastPresent: SteamId -> lần cuối thấy có mặt (giờ máy). Chỉ báo khi người đó KHÔNG có mặt trong `gap` trước đó
    // -> không báo lại khi script cắt sang file mới, hay khi ai đó lấp ló ở rìa tầm nhìn.
    public List<DinoAlert> CheckNewDinos(Dictionary<string, DateTime> lastPresent, DateTime now, TimeSpan gap)
    {
        var res = new List<DinoAlert>();
        Actor me = null; Func<Actor, string> owner = null; bool meFound = false;
        ClassifyNew();
        foreach (var a in dinoActors) // = actor có Species(a) != null, theo thứ tự AllActors
        {
            string sid = Unq(a.Get("SteamId"));
            // lấy dino ra từ gara: báo riêng 1 lần (kể cả khi kênh đã đóng ngay), không báo thêm "vào vùng"
            if (a.GarageOut != null && !a.GarageAnnounced)
            {
                a.GarageAnnounced = true;
                if (sid != "") lastPresent[sid] = now;
                if (CurTime - a.GarageOut[0] > 20) continue; // sự kiện cũ (mới bật báo / mở lại tool) -> bỏ
                if (!meFound) { me = FindMe(); owner = MakeOwner(PlayerStates()); meFound = true; }
                res.Add(MakeGarageAlert(a, me, owner));
                continue;
            }
            if (a.CloseTime >= 0) continue;
            if (sid == "" || a.Get("Controller") != "" || a.Get("bIsDead") == "1") continue;
            // vị trí đầu tiên có thể là vị trí giả (dino mới lấy từ gara): đợi 1,6 s xem có nhảy xa không rồi mới báo
            if (a.GarageOut == null && a.Track.Count > 0 && CurTime - a.Track[0][0] < 1.6) continue;
            DateTime last;
            bool recent = lastPresent.TryGetValue(sid, out last) && now - last <= gap;
            lastPresent[sid] = now;
            if (recent) continue;
            if (!meFound) { me = FindMe(); owner = MakeOwner(PlayerStates()); meFound = true; }
            res.Add(MakeAlert(a, me, owner));
        }
        return res;
    }

    DinoAlert MakeGarageAlert(Actor d, Actor me, Func<Actor, string> owner)
    {
        var al = MakeAlert(d, me, owner, d.GarageOut);
        string name = owner(d);
        double g; bool adult = !double.TryParse(d.Get("Growth"), NumberStyles.Float, IC, out g) || g >= 0.5;
        al.Garage = true;
        al.Title = "🏠 " + (name != "" ? name : "Người chơi") + (adult ? " lấy dino ra từ gara" : " xuất hiện mới (hồi sinh)");
        al.Detail = Species(d) + " · " + al.Detail + string.Format(IC, " · tại X {0:0} Y {1:0}", d.GarageOut[1], d.GarageOut[2]);
        return al;
    }

    DinoAlert MakeAlert(Actor d, Actor me, Func<Actor, string> owner, double[] posOverride = null)
    {
        string name = owner(d);
        string gender = d.Get("bIsFemale") == "1" ? "cái" : "đực";
        string growth = d.Get("Growth") != "" ? Pct(d.Get("Growth")) : "";
        var al = new DinoAlert { ReplayTime = CurTime };
        al.Title = Species(d) + (name != "" ? " · " + name : "");
        var parts = new List<string>();
        parts.Add(gender + (growth != "" ? ", " + growth : ""));
        if (me != null && me.Track.Count > 0 && (posOverride != null || d.Track.Count > 0))
        {
            double[] a = me.Track.Last(), b = posOverride ?? d.Track.Last();
            double brg = Bearing(a, b);
            string s = string.Format(IC, "{0:0} m · {1} radar ({2})", Dist(a, b), RadarDir[(int)Math.Round(((brg % 360) + 360) % 360 / 45) % 8], CompassName(brg));
            // so với hướng bạn đang nhìn
            double[] withYaw = me.Track.LastOrDefault(x => x.Length > 4);
            if (withYaw != null)
            {
                double rel = ((brg - YawToCompass(withYaw[4])) % 360 + 540) % 360 - 180, r = Math.Abs(rel);
                string side = rel > 0 ? "phải" : "trái";
                s += " · " + (r <= 20 ? "thẳng trước mặt" : r <= 70 ? "phía trước bên " + side : r <= 110 ? "bên " + side : r <= 160 ? "phía sau bên " + side : "sau lưng");
            }
            parts.Add(s);
        }
        al.Detail = string.Join(" · ", parts.ToArray());
        return al;
    }
}

// Ô thông báo góc màn hình: luôn trên cùng, KHÔNG lấy focus (không làm gián đoạn game / script AHK),
// chuột bấm xuyên qua, tự ẩn sau vài giây. Nhiều thông báo thì xếp chồng xuống dưới.
class AlertToast : Form
{
    static readonly List<AlertToast> open = new List<AlertToast>();
    const int W = 380, H = 64, Gap = 8;

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x20 /*TRANSPARENT: bấm xuyên*/ | 0x08000000 /*NOACTIVATE*/;
            return cp;
        }
    }

    AlertToast(DinoAlert a)
    {
        Color accent = a.Garage ? Color.FromArgb(255, 159, 28) : Color.FromArgb(255, 90, 79);
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 24); Size = new Size(W, H); Opacity = 0.94;
        Controls.Add(new Panel { BackColor = accent, Dock = DockStyle.Left, Width = 6 });
        Controls.Add(new Label { Text = (a.Garage ? "" : "⚠ ") + a.Title, ForeColor = a.Garage ? Color.FromArgb(255, 176, 64) : Color.FromArgb(255, 211, 77), Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, Location = new Point(16, 8), Size = new Size(W - 24, 24) });
        Controls.Add(new Label { Text = a.Detail, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9.5f), AutoSize = false, AutoEllipsis = true, Location = new Point(16, 34), Size = new Size(W - 24, 22) });
        var t = new Timer { Interval = 9000 };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); Close(); };
        t.Start();
        FormClosed += (s, e) => { open.Remove(this); Relayout(); };
    }

    static void Relayout()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        for (int i = 0; i < open.Count; i++) open[i].Location = new Point(wa.Right - W - 16, wa.Top + 16 + i * (H + Gap));
    }

    public static void Show(DinoAlert a)
    {
        while (open.Count >= 5) open[0].Close();   // tối đa 5 ô, bỏ ô cũ nhất
        var f = new AlertToast(a);
        open.Add(f); Relayout();
        f.Show();
    }

    // âm báo riêng (2 nốt) - khác âm của Windows để dễ nhận ra
    static byte[] wavGarage;
    public static void BeepGarage() // 3 nốt đi lên – khác tiếng "vào vùng"
    {
        try
        {
            if (wavGarage == null) wavGarage = MakeTone(new[] { 523.0, 784.0, 1047.0 }, 0.1, 0.35);
            new SoundPlayer(new MemoryStream(wavGarage)).Play();
        }
        catch { SystemSounds.Asterisk.Play(); }
    }
    static byte[] wav;
    public static void Beep()
    {
        try
        {
            if (wav == null) wav = MakeTone(new[] { 880.0, 1175.0 }, 0.12, 0.35);
            new SoundPlayer(new MemoryStream(wav)).Play(); // phát không chặn
        }
        catch { SystemSounds.Exclamation.Play(); }
    }
    static byte[] MakeTone(double[] freqs, double secEach, double vol)
    {
        const int rate = 22050;
        int nEach = (int)(rate * secEach), n = nEach * freqs.Length;
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0x46464952); w.Write(36 + n * 2); w.Write(0x45564157); w.Write(0x20746D66); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(0x61746164); w.Write(n * 2);
        for (int k = 0; k < freqs.Length; k++)
            for (int i = 0; i < nEach; i++)
            {
                double env = Math.Min(1, Math.Min(i, nEach - i) / (rate * 0.01)); // vào/ra mềm, không lách cách
                w.Write((short)(Math.Sin(2 * Math.PI * freqs[k] * i / rate) * env * vol * short.MaxValue));
            }
        w.Flush();
        return ms.ToArray();
    }
}
