using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Nút "Cất gara" trên bản đồ web. CHỈ gọi đúng 3 việc của luồng cất (người dùng dặn "CHỈ CẤT"):
//   POST /api/overlay/garage/park {"step":"start"}     -> {ok,pending,delaySec}   (đếm ngược ~30 s, huỷ được)
//   POST /api/overlay/garage/park {"step":"cancel"}    (chỉ khi huỷ trong lúc đếm ngược)
//   POST /api/overlay/garage/park {"step":"finalize"}  -> {ok,commandId}
//   GET  /api/overlay/garage/status?id=<commandId>     -> {status:"done"|"failed",error?}   (kết quả thật nằm ở đây)
// KHÔNG có slay / sell / restore / rename. Ký HMAC + Bearer như combatlog (PvnSign). Mỗi lúc chỉ 1 lệnh.
// Trạng thái đẩy tới trang qua SSE "park": {"s":"idle|starting|countdown|finalizing|waiting|done|failed|cancelled","left":giây,"msg":"…"}
class PvnPark
{
    public Func<string> TokenProvider;
    public Action<string> Changed;          // JSON trạng thái
    public volatile string State = "idle";
    const string ParkPath = "/api/overlay/garage/park";

    readonly object lk = new object();
    Thread th;
    volatile bool cancel;
    public bool Busy { get { return th != null && th.IsAlive; } }

    // action = "start" | "cancel". Trả về null nếu nhận lệnh, hoặc thông báo lỗi tiếng Việt.
    public string Command(string action)
    {
        lock (lk)
        {
            if (action == "cancel")
            {
                if (!Busy || State != "countdown") return "Không có lệnh cất nào đang đếm ngược để huỷ.";
                cancel = true; return null;
            }
            if (action != "start") return "Lệnh không hợp lệ.";
            if (Busy) return "Đang có một lệnh cất gara chạy rồi.";
            if (!PvnSign.HasKey) return "Chưa có khoá PVN – bấm \"Quét khoá PVN\" ở tab PVN trực tiếp.";
            string token = TokenProvider != null ? TokenProvider() : null;
            if (token == null) return "Chưa đăng nhập PVN Overlay trên máy này (không có token).";
            cancel = false;
            th = new Thread(() => Run(token)) { IsBackground = true, Name = "PvnPark" };
            th.Start();
            return null;
        }
    }

    void Push(string s, int left, string msg)
    {
        State = s;
        var cb = Changed;
        if (cb != null) { try { cb("{\"s\":\"" + s + "\",\"left\":" + left + ",\"msg\":" + Q(msg) + "}"); } catch { } }
    }

    void Run(string token)
    {
        try
        {
            Push("starting", 0, "Đang gửi lệnh cất gara…");
            string r = Post(token, "start");
            if (!Flag(r, "ok")) { Push("failed", 0, Vi(Str(r, "error") ?? r)); return; }
            int delay = (int)Num(r, "delaySec");
            if (Flag(r, "pending") && delay > 0)
            {
                DateTime end = DateTime.UtcNow.AddSeconds(delay);
                int last = -1;
                while (DateTime.UtcNow < end)
                {
                    if (cancel)
                    {
                        try { Post(token, "cancel"); } catch { }
                        Push("cancelled", 0, "Đã huỷ cất gara.");
                        return;
                    }
                    int left = (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds);
                    if (left != last) { last = left; Push("countdown", left, "Đếm ngược cất gara…"); }
                    Thread.Sleep(100);
                }
            }
            if (cancel) { try { Post(token, "cancel"); } catch { } Push("cancelled", 0, "Đã huỷ cất gara."); return; }
            Push("finalizing", 0, "Đang xác nhận cất gara…");
            r = Post(token, "finalize");
            string id = Str(r, "commandId");
            if (!Flag(r, "ok") || string.IsNullOrEmpty(id)) { Push("failed", 0, Vi(Str(r, "error") ?? "Server từ chối cất (hết thời gian chờ hoặc bị huỷ)")); return; }
            // kết quả thật: hỏi status mỗi 1,5 s, tối đa 40 lần (như giao diện PVN)
            for (int i = 0; i < 40; i++)
            {
                Push("waiting", 0, "Đang chờ server cất…");
                Thread.Sleep(1500);
                string st = Get(token, "/api/overlay/garage/status?id=" + id);
                string status = Str(st, "status");
                if (status == "done") { PvnSign.ReportOk(); Push("done", 0, "Đã cất khủng long vào gara."); return; }
                if (status == "failed") { Push("failed", 0, Vi(Str(st, "error") ?? "Cất gara thất bại.")); return; }
            }
            Push("failed", 0, "Hết thời gian chờ server trả kết quả (có thể vẫn đang cất – kiểm tra trong game).");
        }
        catch (Exception e)
        {
            PvnSign.ReportError(e);
            Push("failed", 0, "Lỗi: " + Short(e));
        }
    }

    // POST luồng cất (body {"step":...}); lỗi HTTP vẫn đọc nội dung để lấy thông báo của server
    static string Post(string token, string step)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://islepilot.eu" + ParkPath);
        r.Method = "POST"; r.UserAgent = "DinoReplayReader"; r.Accept = "application/json"; r.ContentType = "application/json"; r.Timeout = 15000;
        PvnSign.Add(r, "POST", ParkPath, token);
        byte[] body = Encoding.UTF8.GetBytes("{\"step\":\"" + step + "\"}");
        r.ContentLength = body.Length;
        using (var rs = r.GetRequestStream()) rs.Write(body, 0, body.Length);
        return Read(r);
    }

    static string Get(string token, string pathAndQuery)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://islepilot.eu" + pathAndQuery);
        r.UserAgent = "DinoReplayReader"; r.Accept = "application/json"; r.Timeout = 15000;
        PvnSign.Add(r, "GET", pathAndQuery, token);
        return Read(r);
    }

    static string Read(HttpWebRequest r)
    {
        try
        {
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var s = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return s.ReadToEnd();
        }
        catch (WebException e)
        {
            var hr = e.Response as HttpWebResponse;
            if (hr == null) throw;
            if ((int)hr.StatusCode == 401 || (int)hr.StatusCode == 403) throw; // khoá/token hỏng -> ReportError xử lý
            using (var s = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) return s.ReadToEnd();
        }
    }

    static string Short(Exception e) { var w = e as WebException; if (w != null && w.Response is HttpWebResponse) return "HTTP " + (int)((HttpWebResponse)w.Response).StatusCode; return e.Message; }

    // thông báo lỗi của server (tiếng Anh) -> tiếng Việt
    static string Vi(string err)
    {
        if (err == null) return "Cất gara thất bại.";
        if (err.IndexOf("too close", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Có người chơi khác quá gần – cần đứng cách họ ≥ 50 m rồi thử lại.";
        return err;
    }

    static string Str(string json, string key)
    {
        if (json == null) return null;
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }
    static bool Flag(string json, string key) { return json != null && Regex.IsMatch(json, "\"" + key + "\"\\s*:\\s*true"); }
    static double Num(string json, string key)
    {
        var m = json == null ? null : Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
        double v; return m != null && m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
    }
    static string Q(string t) { return "\"" + (t ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
}
