using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Máy chủ web nhỏ chỉ trong máy (127.0.0.1):
//  - phục vụ file trong thư mục tool (output\..., maps\...) để mở bản đồ qua http://
//  - /events/<tên thư mục kết quả>: kênh "server-sent events" -> tool đẩy dữ liệu mới vào bản đồ ngay lập tức
class LiveServer
{
    readonly string root;
    TcpListener listener;
    public int Port;
    readonly object lk = new object();
    readonly List<SseClient> clients = new List<SseClient>();
    readonly Dictionary<string, string> latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    class SseClient { public string Name; public TcpClient Tcp; public NetworkStream Stream; }
    DateTime lastPageLog = DateTime.MinValue;

    // Nút "Cất gara" trên trang: POST /park/start | /park/cancel. Chặn trang web lạ (CSRF) bằng 2 lớp: header Origin phải đúng
    // http://127.0.0.1:<cổng> + header X-Park-Token = mã ngẫu nhiên của lần chạy này (trang lấy qua SSE "parktoken"; trang khác
    // không đọc được SSE vì không có CORS, và không thể gửi header tuỳ biến mà không qua preflight – máy chủ không trả lời OPTIONS).
    public Func<string, string> ParkAction;      // trả null = nhận lệnh, hoặc thông báo lỗi
    readonly string parkToken = NewToken();
    static string NewToken()
    {
        var b = new byte[16];
        using (var g = new System.Security.Cryptography.RNGCryptoServiceProvider()) g.GetBytes(b);
        var sb = new StringBuilder(); foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
    volatile string parkJson;
    public void PushPark(string json)
    {
        parkJson = json;
        List<SseClient> targets;
        lock (lk) targets = new List<SseClient>(clients);
        foreach (var cl in targets) SendRaw(cl, "event: park\ndata: " + json + "\n\n");
    }

    bool HandlePark(NetworkStream s, string path, string hdr)
    {
        string h = "\n" + hdr.ToLowerInvariant();
        bool originOk = h.Contains("\norigin: http://127.0.0.1:" + Port + "\r") || h.Contains("\norigin: http://127.0.0.1:" + Port + "\n");
        bool tokenOk = h.Contains("\nx-park-token: " + parkToken + "\r") || h.Contains("\nx-park-token: " + parkToken + "\n");
        string status, body;
        if (!originOk || !tokenOk) { status = "403 Forbidden"; body = "{\"ok\":false,\"error\":\"Không được phép\"}"; }
        else
        {
            string err = ParkAction == null ? "Chưa sẵn sàng" : ParkAction(path == "/park/cancel" ? "cancel" : "start");
            status = "200 OK"; body = err == null ? "{\"ok\":true}" : "{\"ok\":false,\"error\":" + JsonQ(err) + "}";
        }
        byte[] b = Encoding.UTF8.GetBytes(body);
        WriteAscii(s, "HTTP/1.1 " + status + "\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
        s.Write(b, 0, b.Length);
        return true;
    }
    static string JsonQ(string t) { return "\"" + (t ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    public LiveServer(string rootDir) { root = Path.GetFullPath(rootDir).TrimEnd('\\') + "\\"; }

    public bool Start()
    {
        for (int port = 47321; port < 47341; port++)
        {
            try { listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); Port = port; break; }
            catch (SocketException) { listener = null; }
        }
        if (listener == null) return false;
        new Thread(AcceptLoop) { IsBackground = true, Name = "LiveServer" }.Start();
        return true;
    }

    public string MapUrl(string outDir)
    {
        return string.Format("http://127.0.0.1:{0}/output/{1}/map.html", Port, Uri.EscapeDataString(Path.GetFileName(outDir.TrimEnd('\\'))));
    }

    // "/live/...": luôn trỏ tới kết quả của replay đang quay (tool đổi khi chuyển sang file mới)
    public string LiveUrl { get { return string.Format("http://127.0.0.1:{0}/live/map.html", Port); } }
    volatile string liveName;
    public void SetLive(string outName)
    {
        liveName = outName;
        string last;
        lock (lk) latest.TryGetValue(outName, out last);
        if (last != null) Push(outName, last); // trang /live đang mở chuyển sang ngay
    }

    void AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { return; }
            c.NoDelay = true; // gửi ngay, không gom gói (giảm trễ)
            ThreadPool.QueueUserWorkItem(_ => Handle(c));
        }
    }

    void Handle(TcpClient c)
    {
        NetworkStream s = null;
        try
        {
            s = c.GetStream();
            string method, hdr;
            string target = ReadRequestPath(s, out method, out hdr);
            if (target == null) { c.Close(); return; }
            if (method == "POST")
            {
                // chỉ nhận đúng 2 lệnh của nút cất gara; mọi POST khác bỏ
                if (target == "/park/start" || target == "/park/cancel") HandlePark(s, target, hdr);
                try { c.Close(); } catch { }
                return;
            }
            string path = target, query = "";
            int q = target.IndexOf('?'); if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

            // /log?m=<nội dung>: trang bản đồ/radar báo chỗ chậm -> perf_log.txt
            if (path == "/log")
            {
                // chặn thêm phía máy chủ: tối đa 2 lần gửi / giây (trang đã tự gộp, đây chỉ là phòng khi có lỗi)
                bool drop;
                lock (lk) { drop = (DateTime.Now - lastPageLog).TotalMilliseconds < 500; if (!drop) lastPageLog = DateTime.Now; }
                if (drop) query = "";
                foreach (var kv in query.Split('&'))
                    if (kv.StartsWith("m="))
                    {
                        string m = Uri.UnescapeDataString(kv.Substring(2).Replace('+', ' '));
                        if (m.Length > 2000) m = m.Substring(0, 2000);
                        PerfLog.Write(null, "[trang] " + m);
                    }
                WriteAscii(s, "HTTP/1.1 204 No Content\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                try { c.Close(); } catch { }
                return;
            }

            if (path.StartsWith("/events/"))
            {
                string name = Uri.UnescapeDataString(path.Substring(8));
                WriteAscii(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\n");
                var cl = new SseClient { Name = name, Tcp = c, Stream = s };
                string last;
                lock (lk)
                {
                    clients.Add(cl);
                    latest.TryGetValue(name, out last);
                }
                if (last != null) Send(cl, last);
                string ly = layersJson;
                if (ly != null) SendRaw(cl, "event: layers\ndata: " + ly + "\n\n");
                string ht = heatJson;
                if (ht != null) SendRaw(cl, "event: heatmap\ndata: " + ht + "\n\n");
                SendRaw(cl, "event: parktoken\ndata: " + parkToken + "\n\n");
                string pj = parkJson;
                if (pj != null) SendRaw(cl, "event: park\ndata: " + pj + "\n\n");
                string fr = friendsJson;
                if (fr != null) SendRaw(cl, "event: friends\ndata: " + fr + "\n\n");
                string gj = garageJson;
                if (gj != null) SendRaw(cl, "event: garage\ndata: " + gj + "\n\n");
                return; // giữ kết nối mở để đẩy dữ liệu sau
            }

            ServeFile(s, path);
        }
        catch { }
        try { c.Close(); } catch { }
    }

    static string ReadRequestPath(NetworkStream s, out string method, out string headers)
    {
        method = null; headers = "";
        var buf = new byte[8192]; int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf, n, buf.Length - n);
            if (r <= 0) return null;
            n += r;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
        }
        string first = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
        var parts = first.Split(' ');
        if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "POST")) return null;
        method = parts[0];
        headers = Encoding.ASCII.GetString(buf, 0, n);
        return parts[1]; // gồm cả phần ?query (Handle tách ra)
    }

    void ServeFile(NetworkStream s, string urlPath)
    {
        string rel = Uri.UnescapeDataString(urlPath).TrimStart('/').Replace('/', '\\');
        if (rel.StartsWith("live\\", StringComparison.OrdinalIgnoreCase))
        {
            // /live/map.html = khuôn bản đồ; /live/data.js... = file của replay đang quay
            string file = rel.Substring(5), ln = liveName;
            if (file.Equals("map.html", StringComparison.OrdinalIgnoreCase)) rel = "map_template.html";
            else if (ln != null) rel = "output\\" + ln + "\\" + file;
            else if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                byte[] empty = Encoding.UTF8.GetBytes("window.REPLAY_DATA = null;\n");
                WriteAscii(s, "HTTP/1.1 200 OK\r\nContent-Type: application/javascript; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + empty.Length + "\r\nConnection: close\r\n\r\n");
                s.Write(empty, 0, empty.Length);
                return;
            }
        }
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); } catch { full = null; }
        // chỉ phục vụ file nằm trong thư mục tool
        if (full == null || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            WriteAscii(s, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            return;
        }
        string ext = Path.GetExtension(full).ToLowerInvariant();
        string type = ext == ".html" ? "text/html; charset=utf-8" : ext == ".js" ? "application/javascript; charset=utf-8"
            : ext == ".webp" ? "image/webp" : ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
            : ext == ".csv" || ext == ".txt" ? "text/plain; charset=utf-8" : "application/octet-stream";
        // ảnh bản đồ không đổi -> cho trình duyệt giữ lại; dữ liệu thì luôn lấy mới
        string cache = ext == ".webp" || ext == ".png" || ext == ".jpg" ? "max-age=86400" : "no-store";
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            WriteAscii(s, string.Format("HTTP/1.1 200 OK\r\nContent-Type: {0}\r\nContent-Length: {1}\r\nCache-Control: {2}\r\nConnection: close\r\n\r\n", type, fs.Length, cache));
            fs.CopyTo(s);
        }
    }

    // Đẩy dữ liệu bản đồ mới (JSON 1 dòng) tới mọi trang đang mở kết quả `name`
    public void Push(string name, string json)
    {
        List<SseClient> targets;
        bool isLive = string.Equals(name, liveName, StringComparison.OrdinalIgnoreCase);
        lock (lk)
        {
            latest[name] = json;
            if (isLive) latest["live"] = json;
            // trang mở theo tên thư mục + trang /live (nếu đây là replay đang quay)
            targets = clients.FindAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) || (isLive && x.Name == "live"));
        }
        foreach (var cl in targets) Send(cl, json);
    }

    // Lớp bản đồ bật/tắt (do tool chọn): đẩy tới MỌI trang đang mở (bản đồ, /live, overlay) dưới dạng sự kiện "layers"
    volatile string layersJson;
    public void PushLayers(string json)
    {
        layersJson = json;
        List<SseClient> targets;
        lock (lk) targets = new List<SseClient>(clients);
        foreach (var cl in targets) SendRaw(cl, "event: layers\ndata: " + json + "\n\n");
    }

    // Bản đồ nhiệt PVN (JSON của API, hoặc "null" = xoá): đẩy tới mọi trang, trang mở sau nhận bản mới nhất
    volatile string heatJson;
    public void PushHeat(string json)
    {
        heatJson = json;
        List<SseClient> targets;
        lock (lk) targets = new List<SseClient>(clients);
        foreach (var cl in targets) SendRaw(cl, "event: heatmap\ndata: " + json + "\n\n");
    }

    // Tên bạn bè/group (mảng JSON, chữ thường) -> chấm xanh; trang mở sau nhận bản mới nhất
    volatile string friendsJson;
    public void PushFriends(string json)
    {
        friendsJson = json;
        List<SseClient> targets;
        lock (lk) targets = new List<SseClient>(clients);
        foreach (var cl in targets) SendRaw(cl, "event: friends\ndata: " + json + "\n\n");
    }

    // Gara PVN (mảng JSON dinos) -> điểm đánh dấu trên bản đồ; trang mở sau nhận bản mới nhất
    volatile string garageJson;
    public void PushGarage(string json)
    {
        garageJson = json;
        List<SseClient> targets;
        lock (lk) targets = new List<SseClient>(clients);
        foreach (var cl in targets) SendRaw(cl, "event: garage\ndata: " + json + "\n\n");
    }

    // Số trang (bản đồ / overlay) đang nối với tool – không có trang nào thì khỏi gọi API PVN
    public int ClientCount { get { lock (lk) return clients.Count; } }

    // Sự kiện một lần (không lưu cho trang mở sau), vd "zoom" = in/out từ phím tắt Ctrl+F10/F11
    public void PushEvent(string name, string data)
    {
        List<SseClient> targets;
        lock (lk) targets = new List<SseClient>(clients);
        foreach (var cl in targets) SendRaw(cl, "event: " + name + "\ndata: " + data + "\n\n");
    }

    void Send(SseClient cl, string json) { SendRaw(cl, "data: " + json + "\n\n"); }

    void SendRaw(SseClient cl, string text)
    {
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            lock (cl) cl.Stream.Write(b, 0, b.Length);
        }
        catch
        {
            lock (lk) clients.Remove(cl); // trang đã đóng
            try { cl.Tcp.Close(); } catch { }
        }
    }

    static void WriteAscii(Stream s, string text) { byte[] b = Encoding.UTF8.GetBytes(text); s.Write(b, 0, b.Length); }
}
