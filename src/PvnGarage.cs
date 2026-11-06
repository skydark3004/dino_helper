using System;
using System.IO;
using System.Net;
using System.Threading;

// Gara PVN (khủng long đã cất của bạn): GET islepilot.eu/api/overlay/garage (ký HMAC như combatlog, chỉ ĐỌC –
// không bao giờ gọi park/slay). Mỗi dino có parkPos {x,y} (toạ độ game = toạ độ bản đồ), parkedAt, growth, đột biến…
// Chỉ gọi khi có trang bản đồ/overlay đang mở (ClientCount > 0), mỗi 30 s. Gửi nguyên mảng "dinos" cho trang (JSON.parse ở trang).
class PvnGarage
{
    public Func<string> TokenProvider;
    public Func<bool> Enabled;
    public Func<int> ClientCount;
    public Action<string> Changed;      // JSON mảng dinos
    public volatile string State = "";
    public const int PollMs = 30000;

    volatile bool stop;
    Thread th;
    string lastJson;

    public bool Running { get { return th != null && th.IsAlive; } }

    public void Start()
    {
        if (Running) return;
        stop = false;
        th = new Thread(Loop) { IsBackground = true, Name = "PvnGarage" };
        th.Start();
    }

    public void Stop() { stop = true; }

    void Loop()
    {
        DateTime last = DateTime.MinValue;
        bool hadClients = false;
        while (!stop)
        {
            bool on = (Enabled == null || Enabled()) && PvnSign.HasKey && ClientCount != null && ClientCount() > 0;
            if (!on) { hadClients = false; Sleep(1000); continue; }
            string token = TokenProvider != null ? TokenProvider() : null;
            if (token == null) { Sleep(2000); continue; }
            // trang vừa mở -> gọi ngay; sau đó mỗi 30 s
            if (!hadClients || (DateTime.UtcNow - last).TotalMilliseconds >= PollMs)
            {
                hadClients = true;
                last = DateTime.UtcNow;
                try { Poll(token); } catch (Exception e) { State = "lỗi: " + Short(e); PvnSign.ReportError(e); }
            }
            Sleep(1000);
        }
    }

    static string Short(Exception e) { var w = e as WebException; if (w != null && w.Response is HttpWebResponse) return "HTTP " + (int)((HttpWebResponse)w.Response).StatusCode; return e.Message; }
    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

    void Poll(string token)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://islepilot.eu/api/overlay/garage");
        r.UserAgent = "DinoReplayReader";
        r.Accept = "application/json";
        r.Timeout = 10000;
        PvnSign.Add(r, "GET", "/api/overlay/garage", token);
        string body;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var s = new StreamReader(resp.GetResponseStream()))
            body = s.ReadToEnd();
        PvnSign.ReportOk();
        string arr = ExtractDinos(body);
        State = "ok " + DateTime.Now.ToString("HH:mm:ss");
        if (arr == null || arr == lastJson) return;
        lastJson = arr;
        if (Changed != null) { try { Changed(arr); } catch { } }
    }

    // Lấy chuỗi mảng JSON sau "dinos": (đếm ngoặc, bỏ qua ngoặc trong chuỗi)
    public static string ExtractDinos(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        int i = body.IndexOf("\"dinos\"", StringComparison.Ordinal);
        if (i < 0) return null;
        int a = body.IndexOf('[', i);
        if (a < 0) return null;
        int depth = 0; bool inStr = false;
        for (int k = a; k < body.Length; k++)
        {
            char c = body[k];
            if (inStr) { if (c == '\\') k++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) return body.Substring(a, k - a + 1); }
        }
        return null;
    }
}
