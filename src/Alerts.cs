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
            if (a.CloseTime >= 0) continue;
            string sid = Unq(a.Get("SteamId"));
            if (sid == "" || a.Get("Controller") != "" || a.Get("bIsDead") == "1") continue;
            DateTime last;
            bool recent = lastPresent.TryGetValue(sid, out last) && now - last <= gap;
            lastPresent[sid] = now;
            if (recent) continue;
            if (!meFound) { me = FindMe(); owner = MakeOwner(PlayerStates()); meFound = true; }
            res.Add(MakeAlert(a, me, owner));
        }
        return res;
    }

    DinoAlert MakeAlert(Actor d, Actor me, Func<Actor, string> owner)
    {
        string name = owner(d);
        string gender = d.Get("bIsFemale") == "1" ? "cái" : "đực";
        string growth = d.Get("Growth") != "" ? Pct(d.Get("Growth")) : "";
        var al = new DinoAlert { ReplayTime = CurTime };
        al.Title = Species(d) + (name != "" ? " · " + name : "");
        var parts = new List<string>();
        parts.Add(gender + (growth != "" ? ", " + growth : ""));
        if (me != null && me.Track.Count > 0 && d.Track.Count > 0)
        {
            double[] a = me.Track.Last(), b = d.Track.Last();
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
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 24); Size = new Size(W, H); Opacity = 0.94;
        Controls.Add(new Panel { BackColor = Color.FromArgb(255, 90, 79), Dock = DockStyle.Left, Width = 6 });
        Controls.Add(new Label { Text = "⚠ " + a.Title, ForeColor = Color.FromArgb(255, 211, 77), Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, Location = new Point(16, 8), Size = new Size(W - 24, 24) });
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
