using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Bạn bè / group của bạn trên PVN: GET /api/overlay/map (ký HMAC như combatlog) mỗi 10 s,
// lấy nhãn (label) của các marker không phải bạn (self != true). Người chơi nào trùng tên -> vẽ chấm XANH thay vì đỏ.
class PvnFriends
{
    public Func<string> TokenProvider;
    public Func<bool> Enabled;
    public Action<string> Changed;      // JSON mảng tên (chữ thường), gọi khi danh sách đổi
    public volatile string State = "";
    public int PollMs = 10000;

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

    public void Stop() { stop = true; Set(new HashSet<string>()); }

    void Loop()
    {
        while (!stop)
        {
            bool on = Enabled == null || Enabled();
            string token = on && PvnSign.HasKey && TokenProvider != null ? TokenProvider() : null;
            if (token == null) { if (!on) Set(new HashSet<string>()); Sleep(2000); continue; }
            try { Poll(token); } catch (Exception e) { State = "lỗi: " + e.Message; PvnSign.ReportError(e); }
            Sleep(PollMs);
        }
    }

    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

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
        Set(Parse(body));
    }

    // "markers":[ {x,y,yaw,label,self,...}, ... ] – chỉ lấy marker phẳng có "label" và không phải self
    static readonly Regex MarkerRx = new Regex("\\{[^{}]*\"label\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"[^{}]*\\}", RegexOptions.Compiled);
    static readonly Regex LabelRx = new Regex("\"label\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex SelfRx = new Regex("\"self\"\\s*:\\s*true", RegexOptions.Compiled);

    static HashSet<string> Parse(string body)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(body)) return set;
        int i = body.IndexOf("\"markers\"", StringComparison.Ordinal);
        if (i < 0) return set;
        int a = body.IndexOf('[', i);
        if (a < 0) return set;
        // cắt tới dấu ] khớp (bỏ qua ] trong chuỗi)
        int depth = 0, end = body.Length; bool inStr = false;
        for (int k = a; k < body.Length; k++)
        {
            char c = body[k];
            if (inStr) { if (c == '\\') k++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) { end = k; break; } }
        }
        string arr = body.Substring(a, end - a + 1);
        foreach (Match m in MarkerRx.Matches(arr))
        {
            if (SelfRx.IsMatch(m.Value)) continue;
            var l = LabelRx.Match(m.Value);
            if (!l.Success) continue;
            string name = Regex.Unescape(l.Groups[1].Value).Trim().ToLowerInvariant();
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    void Set(HashSet<string> s)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        var sorted = new List<string>(s); sorted.Sort(StringComparer.Ordinal);
        foreach (var n in sorted)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(n.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        sb.Append(']');
        string json = sb.ToString();
        names = s;
        if (json == lastJson) return;
        lastJson = json;
        if (Changed != null) { try { Changed(json); } catch { } }
    }
}
