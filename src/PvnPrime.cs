using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

// Nhiệm vụ Prime của khủng long ĐANG DÙNG: GET islepilot.eu/api/overlay/me (ký HMAC, mỗi 10 s) -> prime{eligible,elder,required,total,done,quests[{name,done}]}.
// Hiện trong overlay chỉ số (VitalsOverlay): chưa đủ -> liệt kê nhiệm vụ chưa xong; đủ -> chỉ ghi chú "đã hoàn thành Prime".
class PrimeQuest { public string Name; public bool Done; }
class PrimeInfo
{
    public int Done, Total, Required;
    public bool Eligible, Elder;
    public PrimeQuest[] Quests = new PrimeQuest[0];
    public DateTime At = DateTime.Now;
    // đủ số nhiệm vụ yêu cầu (hoặc đã là Elder) -> coi như hoàn thành
    public bool Complete { get { return Elder || Eligible || (Required > 0 && Done >= Required); } }
}

class PvnPrime
{
    public Func<string> TokenProvider;
    public Func<bool> Enabled;
    public volatile string State = "";
    public volatile PrimeInfo Last;
    public const int PollMs = 10000;

    volatile bool stop;
    Thread th;
    public bool Running { get { return th != null && th.IsAlive; } }

    public void Start()
    {
        if (Running) return;
        stop = false;
        th = new Thread(Loop) { IsBackground = true, Name = "PvnPrime" };
        th.Start();
    }
    public void Stop() { stop = true; Last = null; }

    void Loop()
    {
        while (!stop)
        {
            bool on = (Enabled == null || Enabled()) && PvnSign.HasKey;
            if (!on) { Last = null; Sleep(1000); continue; }
            string token = TokenProvider != null ? TokenProvider() : null;
            if (token == null) { Sleep(2000); continue; }
            try { Poll(token); } catch (Exception e) { State = "lỗi: " + Short(e); PvnSign.ReportError(e); }
            Sleep(PollMs);
        }
    }

    static string Short(Exception e) { var w = e as WebException; if (w != null && w.Response is HttpWebResponse) return "HTTP " + (int)((HttpWebResponse)w.Response).StatusCode; return e.Message; }
    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

    void Poll(string token)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://islepilot.eu/api/overlay/me");
        r.UserAgent = "DinoReplayReader";
        r.Accept = "application/json";
        r.Timeout = 10000;
        PvnSign.Add(r, "GET", "/api/overlay/me", token);
        string body;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var s = new StreamReader(resp.GetResponseStream()))
            body = s.ReadToEnd();
        PvnSign.ReportOk();
        Last = Parse(body);
        State = "ok " + DateTime.Now.ToString("HH:mm:ss");
    }

    static readonly Regex NameRx = new Regex("\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex DoneRx = new Regex("\"done\"\\s*:\\s*(true|false)", RegexOptions.Compiled);
    static readonly Regex ObjRx = new Regex("\\{[^{}]*\\}", RegexOptions.Compiled);

    static int Num(string s, string key)
    {
        var m = Regex.Match(s, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
    static bool Flag(string s, string key) { return Regex.IsMatch(s, "\"" + key + "\"\\s*:\\s*true"); }

    // Tìm object "prime":{…} (đếm ngoặc) rồi đọc các trường; quests là mảng object phẳng {name,done}
    public static PrimeInfo Parse(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = Regex.Match(body, "\"prime\"\\s*:\\s*\\{");
        if (!m.Success) return null;
        int a = m.Index + m.Length - 1, depth = 0, end = -1; bool inStr = false;
        for (int k = a; k < body.Length; k++)
        {
            char c = body[k];
            if (inStr) { if (c == '\\') k++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) { end = k; break; } }
        }
        if (end < 0) return null;
        string obj = body.Substring(a, end - a + 1);
        int q = obj.IndexOf("\"quests\"", StringComparison.Ordinal);
        string head = q >= 0 ? obj.Substring(0, q) : obj, qs = q >= 0 ? obj.Substring(q) : "";
        var p = new PrimeInfo { Done = Num(head, "done"), Total = Num(head, "total"), Required = Num(head, "required"), Eligible = Flag(head, "eligible"), Elder = Flag(head, "elder") };
        var list = new List<PrimeQuest>();
        foreach (Match o in ObjRx.Matches(qs))
        {
            var n = NameRx.Match(o.Value); var d = DoneRx.Match(o.Value);
            if (!n.Success) continue;
            list.Add(new PrimeQuest { Name = Regex.Unescape(n.Groups[1].Value), Done = d.Success && d.Groups[1].Value == "true" });
        }
        p.Quests = list.ToArray();
        if (p.Total == 0) p.Total = p.Quests.Length;
        return p;
    }

    static readonly Dictionary<string, string> Vi = new Dictionary<string, string>
    {
        { "Visit a Sanctuary as a juvenile", "Đến Sanctuary khi còn juvenile" },
        { "Get nested in", "Được làm tổ (nested in)" },
        { "Get perfect diet (1% of each)", "Ăn đủ dinh dưỡng (mỗi loại ≥ 1%)" },
        { "Visit Mass Migration zone", "Đến vùng Mass Migration" },
        { "Visit 2 Migration zones", "Đến 2 vùng Migration" },
        { "Visit 4 Patrol zones", "Đến 4 vùng Patrol" },
        { "Never be Infertile", "Không bị vô sinh (Infertile)" },
        { "Never get Muscle spasms", "Không bị co cơ (Muscle spasms)" },
        { "Raise children to Subadult", "Nuôi con tới Subadult" },
        { "Be a Hypsi, Troodon, Beipi, Dryo or Deino", "Là Hypsi/Troodon/Beipi/Dryo/Deino" },
    };
    public static string Viet(string name) { string v; return name != null && Vi.TryGetValue(name, out v) ? v : name; }
}
