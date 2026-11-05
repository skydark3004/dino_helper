using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Chỉ số khủng long của bạn (máu, thể lực, đói, khát, blood, chảy máu, gãy xương…) từ WebSocket /ows của PVN Overlay
// (islepilot.eu), ~4 frame/giây. Dùng token đăng nhập của PVN Overlay trên máy này:
//   %APPDATA%\pvn-isle-overlay\pvn-isle-overlay.settings.json -> "overlayToken": "enc1:<base64>" (Electron safeStorage)
//   = "v10" + nonce 12 byte + bản mã + tag 16 byte, AES-256-GCM; khoá ở "Local State" -> os_crypt.encrypted_key
//   = "DPAPI" + khoá bọc DPAPI (CurrentUser). Chỉ giải mã được trên đúng máy + tài khoản Windows đó.
// /ows chỉ cần header "Authorization: Bearer <token>" (không cần chữ ký HMAC như /api/overlay/*).
// Mỗi token chỉ 1 kết nối: kết nối mới đá kết nối cũ (close 4000 "replaced") -> bị đá thì DỪNG, không nối lại (tránh đá nhau với PVN Overlay).
static class PvnToken
{
    public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pvn-isle-overlay"); } }

    // Trả về token (null + err nếu không lấy được). baseUrl mặc định https://islepilot.eu
    // steamId của bạn (để combatlog biết đòn nào liên quan tới bạn). null nếu chưa đăng nhập.
    public static string SteamId()
    {
        try
        {
            string sf = Path.Combine(Dir, "pvn-isle-overlay.settings.json");
            if (!File.Exists(sf)) return null;
            var m = Regex.Match(File.ReadAllText(sf), "\"steamId\"\\s*:\\s*\"(\\d+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    public static string Load(out string baseUrl, out string err)
    {
        baseUrl = "https://islepilot.eu"; err = null;
        try
        {
            string sf = Path.Combine(Dir, "pvn-isle-overlay.settings.json");
            if (!File.Exists(sf)) { err = "chưa thấy PVN Overlay trên máy này (không có " + sf + ")"; return null; }
            string s = File.ReadAllText(sf);
            var m = Regex.Match(s, "\"apiBaseUrl\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) baseUrl = m.Groups[1].Value.TrimEnd('/');
            m = Regex.Match(s, "\"overlayToken\"\\s*:\\s*\"([^\"]*)\"");
            if (!m.Success || m.Groups[1].Value.Length == 0) { err = "PVN Overlay chưa đăng nhập (mở PVN Overlay, đăng nhập Steam 1 lần)"; return null; }
            string stored = m.Groups[1].Value;
            if (!stored.StartsWith("enc1:")) return stored; // token không mã hoá

            byte[] blob = Convert.FromBase64String(stored.Substring(5));
            if (blob.Length < 3 + 12 + 16 || blob[0] != 'v' || blob[1] != '1' || blob[2] != '0') { err = "token PVN có định dạng lạ"; return null; }

            string ls = File.ReadAllText(Path.Combine(Dir, "Local State"));
            m = Regex.Match(ls, "\"encrypted_key\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) { err = "không thấy khoá trong Local State của PVN Overlay"; return null; }
            byte[] wrapped = Convert.FromBase64String(m.Groups[1].Value);
            if (wrapped.Length < 6 || Encoding.ASCII.GetString(wrapped, 0, 5) != "DPAPI") { err = "khoá PVN Overlay có định dạng lạ"; return null; }
            byte[] dp = new byte[wrapped.Length - 5]; Buffer.BlockCopy(wrapped, 5, dp, 0, dp.Length);
            byte[] key = ProtectedData.Unprotect(dp, null, DataProtectionScope.CurrentUser);

            byte[] nonce = new byte[12], tag = new byte[16], ct = new byte[blob.Length - 3 - 12 - 16];
            Buffer.BlockCopy(blob, 3, nonce, 0, 12);
            Buffer.BlockCopy(blob, 15, ct, 0, ct.Length);
            Buffer.BlockCopy(blob, blob.Length - 16, tag, 0, 16);
            byte[] plain = AesGcmDecrypt(key, nonce, ct, tag);
            if (plain == null) { err = "không giải mã được token PVN (đăng nhập lại PVN Overlay?)"; return null; }
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) { err = "không đọc được token PVN: " + e.Message; return null; }
    }

    // ---- AES-GCM qua bcrypt.dll (.NET Framework 4 không có lớp AesGcm) ----
    [StructLayout(LayoutKind.Sequential)]
    struct AuthInfo
    {
        public int cbSize, dwInfoVersion;
        public IntPtr pbNonce; public int cbNonce;
        public IntPtr pbAuthData; public int cbAuthData;
        public IntPtr pbTag; public int cbTag;
        public IntPtr pbMacContext; public int cbMacContext;
        public int cbAAD; public long cbData; public int dwFlags;
    }
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr h, string alg, string impl, int flags);
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr h, string prop, byte[] input, int size, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objSize, byte[] secret, int secretSize, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr key, byte[] input, int inSize, ref AuthInfo pad, byte[] iv, int ivSize, byte[] output, int outSize, out int result, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
    [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr h, int flags);

    static byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ct, byte[] tag)
    {
        IntPtr alg = IntPtr.Zero, hk = IntPtr.Zero;
        GCHandle gn = GCHandle.Alloc(nonce, GCHandleType.Pinned), gt = GCHandle.Alloc(tag, GCHandleType.Pinned);
        try
        {
            if (BCryptOpenAlgorithmProvider(out alg, "AES", null, 0) != 0) return null;
            byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            if (BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0) != 0) return null;
            if (BCryptGenerateSymmetricKey(alg, out hk, IntPtr.Zero, 0, key, key.Length, 0) != 0) return null;
            var info = new AuthInfo
            {
                cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1,
                pbNonce = gn.AddrOfPinnedObject(), cbNonce = nonce.Length,
                pbTag = gt.AddrOfPinnedObject(), cbTag = tag.Length,
            };
            byte[] outb = new byte[ct.Length]; int n;
            if (BCryptDecrypt(hk, ct, ct.Length, ref info, null, 0, outb, outb.Length, out n, 0) != 0) return null; // sai tag -> lỗi
            if (n != outb.Length) Array.Resize(ref outb, n);
            return outb;
        }
        finally
        {
            gn.Free(); gt.Free();
            if (hk != IntPtr.Zero) BCryptDestroyKey(hk);
            if (alg != IntPtr.Zero) BCryptCloseAlgorithmProvider(alg, 0);
        }
    }
}

// Kết nối /ows trên luồng nền, tự nối lại (1 → 15 s). Frame "live" -> Last (overlay chỉ số đọc mỗi 250 ms, không cần trang web).
class PvnLive
{
    public volatile string State = "tắt"; // để giao diện hiện (đọc mỗi giây)
    public volatile bool Ok;              // đang nhận dữ liệu
    public volatile bool Kicked;          // bị kết nối khác (PVN Overlay) đá ra -> đã dừng
    public DateTime LastFrame = DateTime.MinValue;
    public int Frames;                    // tổng số frame live
    public volatile Vitals Last;          // frame gần nhất (null = chưa có / đã dừng)

    Thread th; volatile bool stop; ClientWebSocket cur;

    public bool Running { get { return th != null && th.IsAlive; } }

    public void Start()
    {
        if (Running) return;
        stop = false; Kicked = false;
        th = new Thread(Loop) { IsBackground = true, Name = "PvnLive" };
        th.Start();
    }

    public void Stop()
    {
        stop = true; Ok = false; Last = null; State = "tắt";
        var w = cur; if (w != null) try { w.Abort(); } catch { }
    }

    void Loop()
    {
        int backoff = 1000;
        while (!stop)
        {
            string baseUrl, err;
            string token = PvnToken.Load(out baseUrl, out err);
            if (token == null) { State = err; Ok = false; Sleep(15000); continue; }
            string url = Regex.Replace(baseUrl, "^http", "ws", RegexOptions.IgnoreCase) + "/ows";
            string why = Session(url, token);
            Ok = false;
            if (stop) break;
            if (Kicked) { State = why; break; }
            State = why + " – thử lại sau " + (backoff / 1000) + " s";
            Sleep(backoff);
            backoff = Math.Min(backoff * 2, 15000);
            if (why == null) backoff = 1000;
        }
        Ok = false; Last = null;
    }

    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

    // 1 phiên kết nối; trả về lý do kết thúc
    string Session(string url, string token)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        var ws = new ClientWebSocket();
        cur = ws;
        try
        {
            ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            State = "đang kết nối…";
            using (var cts = new CancellationTokenSource(10000))
                ws.ConnectAsync(new Uri(url), cts.Token).Wait();
            State = "đã kết nối, chờ dữ liệu…";
            byte[] hello = Encoding.UTF8.GetBytes("{\"t\":\"hello\",\"name\":\"\"}");
            ws.SendAsync(new ArraySegment<byte>(hello), WebSocketMessageType.Text, true, CancellationToken.None).Wait();

            var buf = new byte[64 * 1024]; var msg = new MemoryStream();
            while (!stop)
            {
                var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    int code = ws.CloseStatus.HasValue ? (int)ws.CloseStatus.Value : 0;
                    string desc = ws.CloseStatusDescription ?? "";
                    if (code == 4000 || desc == "replaced")
                    {
                        Kicked = true;
                        return "bị ngắt vì tài khoản PVN vừa kết nối ở nơi khác (PVN Overlay đang mở?) → đã dừng; tắt PVN Overlay rồi bật lại ô kết nối";
                    }
                    return "máy chủ đóng kết nối (" + code + (desc.Length > 0 ? " " + desc : "") + ")";
                }
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                if (r.MessageType == WebSocketMessageType.Text) Handle(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
                msg.SetLength(0);
            }
            return null;
        }
        catch (Exception e)
        {
            if (stop) return null;
            Exception x = e; while (x.InnerException != null) x = x.InnerException;
            if (x.Message.Contains("401") || x.Message.Contains("403")) return "token PVN bị từ chối (đăng nhập lại PVN Overlay)";
            return "lỗi kết nối: " + x.Message;
        }
        finally
        {
            cur = null;
            try { ws.Dispose(); } catch { }
        }
    }

    void Handle(string text)
    {
        // chỉ cần frame live: {"t":"live","d":{...}}
        if (text.IndexOf("\"t\":\"live\"", StringComparison.Ordinal) < 0) return;
        int i = text.IndexOf("\"d\":{", StringComparison.Ordinal), j = text.LastIndexOf('}');
        if (i < 0 || j <= i + 4 || text[j - 1] != '}') return;
        Last = Vitals.Parse(text.Substring(i + 4, j - (i + 4)));
        Frames++; LastFrame = DateTime.Now; Ok = true;
        State = "đang nhận";
    }
}

// Số liệu 1 frame "live" (đã tách từ JSON). Không đổi sau khi tạo -> luồng giao diện đọc được.
class Vitals
{
    public DateTime At;
    public bool HasDino, Sick, Female;
    public string Species = "";   // "Tyrannosaurus" (từ className BP_Tyrannosaurus_C)
    public double Growth, Health, MaxHealth, Blood, MaxBlood, Stamina, MaxStamina, Hunger, MaxHunger, Thirst, MaxThirst, Oxygen, MaxOxygen, GlassBonesSec, Bacteria;
    public string[] Debuffs = new string[0];
    public bool[] Fractured = new bool[3];      // chân, thân, đầu
    public double[] RecoverPct = new double[3];
    public bool Bleeding { get { return Array.IndexOf(Debuffs, "bleeding") >= 0; } }

    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static double Num(string d, string key)
    {
        var m = Regex.Match(d, "\"" + key + "\":(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
        double v; return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, IC, out v) ? v : 0;
    }
    static bool Flag(string d, string key) { return d.Contains("\"" + key + "\":true"); }

    public static Vitals Parse(string d)
    {
        var v = new Vitals { At = DateTime.Now };
        v.HasDino = Flag(d, "hasDino"); v.Sick = Flag(d, "sick"); v.Female = Flag(d, "isFemale");
        var m = Regex.Match(d, "\"className\":\"BP_([A-Za-z0-9]+?)(?:_C)?\"");
        if (m.Success) v.Species = m.Groups[1].Value;
        v.Growth = Num(d, "growth");
        v.Health = Num(d, "health"); v.MaxHealth = Num(d, "maxHealth");
        v.Blood = Num(d, "blood"); v.MaxBlood = Num(d, "maxBlood");
        v.Stamina = Num(d, "stamina"); v.MaxStamina = Num(d, "maxStamina");
        v.Hunger = Num(d, "hunger"); v.MaxHunger = Num(d, "maxHunger");
        v.Thirst = Num(d, "thirst"); v.MaxThirst = Num(d, "maxThirst");
        v.Oxygen = Num(d, "oxygen"); v.MaxOxygen = Num(d, "maxOxygen");
        v.GlassBonesSec = Num(d, "glassBonesSec"); v.Bacteria = Num(d, "bacteria");
        m = Regex.Match(d, "\"debuffs\":\\[([^\\]]*)\\]");
        if (m.Success)
        {
            var l = new System.Collections.Generic.List<string>();
            foreach (Match x in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"")) l.Add(x.Groups[1].Value);
            v.Debuffs = l.ToArray();
        }
        string[] parts = { "legs", "body", "head" };
        for (int i = 0; i < 3; i++)
        {
            m = Regex.Match(d, "\"" + parts[i] + "\":\\{\"fractured\":(true|false),\"recoverPct\":([0-9.]+)");
            if (!m.Success) continue;
            v.Fractured[i] = m.Groups[1].Value == "true";
            double r; double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, IC, out r); v.RecoverPct[i] = r;
        }
        return v;
    }
}
