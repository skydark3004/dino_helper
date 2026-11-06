using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Bạn bè / group của bạn trên PVN (chấm XANH trên bản đồ):
//  - Có cookie PVN (pvn_cookie.dat, cùng cookie của bản đồ nhiệt): GET https://pvn.islepilot.eu/api/p/pvn/map/markers mỗi 5 s
//    (đúng endpoint trang web PVN dùng; chỉ cần cookie). Marker: {steamId,label,x,y,yaw,path:[…],self?}. Lấy cả VỊ TRÍ của bạn bè
//    -> bản đồ vẽ được cả bạn bè ở xa, ngoài tầm replay (replay chỉ có người trong ~500 m).
//  - Không có cookie: GET islepilot.eu/api/overlay/map (ký HMAC như combatlog) mỗi 10 s, chỉ lấy tên (không có vị trí).
class PvnFriend { public string Name, Label, SteamId; public double X = double.NaN, Y = double.NaN, Yaw; }

class PvnFriends
{
    public Func<string> TokenProvider;
    public Func<bool> Enabled;
    public Action<string> Changed;      // JSON mảng bạn bè, gọi khi đổi (vị trí đổi cũng tính)
    public volatile string State = "";
    public int PollMs = 10000;          // nhịp khi chỉ có token overlay (không cookie)
    public const int WebPollMs = 5000;  // nhịp khi có cookie

    static volatile HashSet<string> names = new HashSet<string>();
    public static bool IsFriend(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return names.Contains(name.Trim().ToLowerInvariant());
    }

    volatile bool stop;
    Thread th;
    string lastJson = "[]";

    public bool Running { get { return th != null && th.IsAlive; } }

    public void Start()
    {
        if (Running) return;
        stop = false;
        th = new Thread(Loop) { IsBackground = true, Name = "PvnFriends" };
        th.Start();
    }

    public void Stop() { stop = true; Set(new List<PvnFriend>()); }

    void Loop()
    {
        while (!stop)
        {
            bool on = Enabled == null || Enabled();
            if (!on) { Set(new List<PvnFriend>()); Sleep(2000); continue; }
            string cookie = PvnHeat.Load();
            if (cookie != null)
            {
                int wait = WebPollMs;
                try { if (!PollWeb(cookie)) wait = 15000; }
                catch (Exception e) { State = "lỗi: " + e.Message; wait = 15000; }
                Sleep(wait);
                continue;
            }
            string token = PvnSign.HasKey && TokenProvider != null ? TokenProvider() : null;
            if (token == null) { Sleep(2000); continue; }
            try { Poll(token); } catch (Exception e) { State = "lỗi: " + e.Message; PvnSign.ReportError(e); }
            Sleep(PollMs);
        }
    }

    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

    // Trả false khi ok:false (cookie hết hạn / chưa online trong game) để gọi thưa lại
    bool PollWeb(string cookie)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://pvn.islepilot.eu/api/p/pvn/map/markers");
        r.UserAgent = "DinoReplayReader";
        r.Accept = "*/*";
        r.Timeout = 10000;
        r.Headers["Cookie"] = "islepilot_player=" + cookie;
        string body;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var s = new StreamReader(resp.GetResponseStream()))
            body = s.ReadToEnd();
        if (body.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0)
        {
            State = "PVN trả ok:false (cookie hết hạn hoặc chưa online trong game)";
            Set(new List<PvnFriend>());
            return false;
        }
        var list = ParseMarkers(body);
        State = "ok " + DateTime.Now.ToString("HH:mm:ss") + " – " + list.Count + " bạn bè";
        Set(list);
        return true;
    }

    // Bản cũ (không cookie): /api/overlay/map ký HMAC, chỉ lấy tên
    void Poll(string token)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://islepilot.eu/api/overlay/map");
        r.UserAgent = "DinoReplayReader";
        r.Accept = "application/json";
        r.Timeout = 10000;
        PvnSign.Add(r, "GET", "/api/overlay/map", token);
        string body;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var s = new StreamReader(resp.GetResponseStream()))
            body = s.ReadToEnd();
        State = "ok " + DateTime.Now.ToString("HH:mm:ss");
        PvnSign.ReportOk();
        var lst = new List<PvnFriend>();
        foreach (var f in ParseMarkers(body)) { f.X = double.NaN; f.Y = double.NaN; lst.Add(f); } // overlay/map: không dùng vị trí
        Set(lst);
    }

    static readonly Regex LabelRx = new Regex("\"label\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex SelfRx = new Regex("\"self\"\\s*:\\s*true", RegexOptions.Compiled);
    static readonly Regex SteamRx = new Regex("\"steamId\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly Regex NumRx = new Regex("\"(x|y|yaw)\"\\s*:\\s*(-?[0-9.eE+-]+)", RegexOptions.Compiled);

    // Duyệt từng marker (object mức trên cùng của mảng "markers"; "path" lồng nhau nên phải đếm ngoặc), bỏ marker self
    public static List<PvnFriend> ParseMarkers(string body)
    {
        var list = new List<PvnFriend>();
        if (string.IsNullOrEmpty(body)) return list;
        int i = body.IndexOf("\"markers\"", StringComparison.Ordinal);
        if (i < 0) return list;
        int a = body.IndexOf('[', i);
        if (a < 0) return list;
        int depth = 0, objStart = -1; bool inStr = false;
        for (int k = a + 1; k < body.Length; k++)
        {
            char c = body[k];
            if (inStr) { if (c == '\\') k++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') { if (depth == 0) objStart = k; depth++; }
            else if (c == '}') { depth--; if (depth == 0 && objStart >= 0) { AddMarker(list, body.Substring(objStart, k - objStart + 1)); objStart = -1; } }
            else if (c == ']' && depth == 0) break;
        }
        return list;
    }

    static void AddMarker(List<PvnFriend> list, string obj)
    {
        // bỏ mảng "path" (các điểm {x,y} làm rối x/y của marker)
        int p = obj.IndexOf("\"path\"", StringComparison.Ordinal);
        if (p >= 0)
        {
            int b = obj.IndexOf('[', p), e = -1;
            if (b >= 0)
            {
                int d = 0;
                for (int k = b; k < obj.Length; k++) { if (obj[k] == '[') d++; else if (obj[k] == ']') { d--; if (d == 0) { e = k; break; } } }
            }
            if (b >= 0 && e > b) obj = obj.Substring(0, p) + obj.Substring(e + 1);
        }
        if (SelfRx.IsMatch(obj)) return;
        var l = LabelRx.Match(obj);
        if (!l.Success) return;
        var f = new PvnFriend();
        f.Label = Regex.Unescape(l.Groups[1].Value).Trim();
        f.Name = f.Label.ToLowerInvariant();
        if (f.Name.Length == 0) return;
        var sm = SteamRx.Match(obj); f.SteamId = sm.Success ? sm.Groups[1].Value : "";
        foreach (Match m in NumRx.Matches(obj))
        {
            double v;
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
            string key = m.Groups[1].Value;
            if (key == "x") f.X = v; else if (key == "y") f.Y = v; else f.Yaw = v;
        }
        list.Add(f);
    }

    // JSON gửi trang: [{"n":tên thường,"l":tên hiển thị,"s":steamId,"x":..,"y":..,"h":yaw}] (x/y/h bỏ nếu không có vị trí)
    void Set(List<PvnFriend> list)
    {
        var set = new HashSet<string>();
        var sorted = new List<PvnFriend>(list);
        sorted.Sort((p, q) => string.CompareOrdinal(p.Name, q.Name));
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var f in sorted)
        {
            set.Add(f.Name);
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"n\":").Append(Q(f.Name)).Append(",\"l\":").Append(Q(f.Label)).Append(",\"s\":").Append(Q(f.SteamId));
            if (!double.IsNaN(f.X) && !double.IsNaN(f.Y))
                sb.Append(string.Format(CultureInfo.InvariantCulture, ",\"x\":{0:0},\"y\":{1:0},\"h\":{2:0}", f.X, f.Y, f.Yaw));
            sb.Append('}');
        }
        sb.Append(']');
        string json = sb.ToString();
        names = set;
        if (json == lastJson) return;
        lastJson = json;
        if (Changed != null) { try { Changed(json); } catch { } }
    }

    static string Q(string t) { return "\"" + (t ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
}
