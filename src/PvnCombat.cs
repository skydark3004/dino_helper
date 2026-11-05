using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Combat: máu đối thủ khi đang đánh nhau với bạn. Nguồn duy nhất = HTTP /api/overlay/combatlog của PVN (islepilot.eu),
// hỏi mỗi 2 s. Chỉ gồm các đòn LIÊN QUAN TỚI BẠN (bạn đánh / bị đánh) -> chỉ biết máu con đối thủ mà bạn đã đánh trúng.
// Khác /ows và heatmap: combatlog BẮT BUỘC ký HMAC (X-Overlay-Sig) ngoài Bearer token.
//   sig = HMAC_SHA256(key, "<ts>.<METHOD>.<path+query>") hex; header X-Overlay-Client: PVN, X-Overlay-Ts: <ms>.
// Khoá HMAC KHÔNG để trong mã nguồn (repo dino_helper tự build + release công khai -> lộ). Lấy tự động theo thứ tự:
//   1) pvn_hmac.txt (64 hex) cạnh exe nếu có (ghi đè thủ công; .gitignore).
//   2) Trích từ bản PVN-Overlay đã cài: %LOCALAPPDATA%\Programs\PVN-Overlay\resources\app.asar
//      -> file electron/overlay-key.generated.cjs. Máy dev nào đã cài PVN-Overlay là có sẵn khoá này.
// Không lấy được -> combatlog không gọi được (tính năng combat tự tắt), phần khác không ảnh hưởng.
static class PvnSign
{
    static string key;
    // Lúc mở tool: CHỈ đọc pvn_hmac.txt (tức thì, không dò asar/tiến trình). Việc dò nặng để cho nút "Quét khoá PVN".
    public static string HmacKey
    {
        get
        {
            if (key == null) key = FromFile() ?? "";
            return key;
        }
    }
    public static bool HasKey { get { return HmacKey.Length == 64; } }

    // Nút "Quét khoá PVN": dò bản PVN-Overlay đã cài (app.asar) rồi lưu pvn_hmac.txt. Trả về đã có khoá chưa.
    public static bool Scan()
    {
        string k = FromFile() ?? FromAsar();
        key = k ?? "";
        return HasKey;
    }

    static string FromFile()
    {
        try
        {
            string f = Path.Combine(Program.ExeDir, "pvn_hmac.txt");
            if (File.Exists(f))
            {
                var m = Regex.Match(File.ReadAllText(f), "[0-9a-fA-F]{64}");
                if (m.Success) return m.Value.ToLowerInvariant();
            }
        }
        catch { }
        return null;
    }

    // Dò bản PVN-Overlay đã cài ở nhiều nơi (máy bạn bè cài khác chỗ / tên thư mục khác), trích khoá từ app.asar.
    static string FromAsar()
    {
        foreach (string asar in AsarCandidates())
        {
            try
            {
                if (!File.Exists(asar)) continue;
                string k = ExtractAsar(asar);
                if (k != null)
                {
                    // lưu lại để lần sau khỏi dò + vẫn dùng được nếu sau này gỡ PVN-Overlay (file .gitignore)
                    try { File.WriteAllText(Path.Combine(Program.ExeDir, "pvn_hmac.txt"), k); } catch { }
                    return k;
                }
            }
            catch { }
        }
        return null;
    }

    // Dò lười (yield): thử đường dẫn file trước (nhanh); chỉ quét tiến trình nếu không ra (chậm ~0,8 s).
    static IEnumerable<string> AsarCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        string[] names = { "PVN-Overlay", "pvn-isle-overlay", "isle-overlay", "IsleVN-Overlay", "PVN Overlay" };

        // 1) gốc cài thường gặp × tên sản phẩm (đường dẫn cố định -> rất nhanh)
        foreach (var r in roots)
            if (!string.IsNullOrEmpty(r))
                foreach (var nm in names)
                {
                    string p = Path.Combine(r, nm, @"resources\app.asar");
                    if (seen.Add(p)) yield return p;
                }

        // 2) quét thư mục con có tên liên quan trong từng gốc
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            string[] dirs; try { dirs = Directory.GetDirectories(r); } catch { continue; }
            foreach (var d in dirs)
            {
                string b = Path.GetFileName(d).ToLowerInvariant();
                if (b.IndexOf("pvn", StringComparison.Ordinal) < 0 && b.IndexOf("isle", StringComparison.Ordinal) < 0 && b.IndexOf("overlay", StringComparison.Ordinal) < 0) continue;
                string p = Path.Combine(d, @"resources\app.asar");
                if (seen.Add(p)) yield return p;
            }
        }

        // 3) tiến trình overlay đang chạy (chậm nhất -> để cuối)
        System.Diagnostics.Process[] procs; try { procs = System.Diagnostics.Process.GetProcesses(); } catch { yield break; }
        foreach (var pr in procs)
        {
            string n; try { n = pr.ProcessName.ToLowerInvariant(); } catch { continue; }
            if (n.IndexOf("overlay", StringComparison.Ordinal) < 0 && n.IndexOf("pvn", StringComparison.Ordinal) < 0 && n.IndexOf("isle", StringComparison.Ordinal) < 0) continue;
            string p; try { p = Path.Combine(Path.GetDirectoryName(pr.MainModule.FileName), @"resources\app.asar"); } catch { continue; }
            if (seen.Add(p)) yield return p;
        }
    }

    // Đọc 1 file trong app.asar (Electron archive): header = 2 Pickle (size + JSON cây thư mục), rồi vùng data.
    static string ExtractAsar(string asar)
    {
        try
        {
            using (var fs = new FileStream(asar, FileMode.Open, FileAccess.Read))
            {
                byte[] b8 = new byte[8];
                if (!ReadFull(fs, b8)) return null;
                uint headerSize = BitConverter.ToUInt32(b8, 4);      // độ dài Pickle header (JSON cây)
                if (headerSize < 8 || headerSize > 64 * 1024 * 1024) return null;
                byte[] hp = new byte[headerSize];
                if (!ReadFull(fs, hp)) return null;
                uint strLen = BitConverter.ToUInt32(hp, 4);          // độ dài chuỗi JSON
                if (strLen + 8 > headerSize) return null;
                string json = Encoding.UTF8.GetString(hp, 8, (int)strLen);
                long dataStart = 8 + headerSize;

                // mục của file: "overlay-key.generated.cjs":{"size":N,"integrity":{...},"offset":"OFF"}
                // -> offset nằm SAU object integrity lồng nhau, phải quét đúng cặp ngoặc.
                string obj = ObjectAfter(json, "\"overlay-key.generated.cjs\"");
                if (obj == null) return null;
                var ms = Regex.Match(obj, "\"size\"\\s*:\\s*(\\d+)");
                var mo = Regex.Match(obj, "\"offset\"\\s*:\\s*\"(\\d+)\"");
                if (!ms.Success || !mo.Success) return null;
                int fsize = int.Parse(ms.Groups[1].Value);
                long off = long.Parse(mo.Groups[1].Value);
                if (fsize <= 0 || fsize > 1024 * 1024) return null;

                fs.Seek(dataStart + off, SeekOrigin.Begin);
                byte[] fb = new byte[fsize];
                if (!ReadFull(fs, fb)) return null;
                var mk = Regex.Match(Encoding.UTF8.GetString(fb), "[0-9a-fA-F]{64}");
                return mk.Success ? mk.Value.ToLowerInvariant() : null;
            }
        }
        catch { return null; }
    }

    // object JSON `{...}` ngay sau khoá `key` (quét đúng cặp ngoặc, bỏ qua object lồng như "integrity")
    static string ObjectAfter(string json, string key)
    {
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int br = json.IndexOf('{', i + key.Length);
        if (br < 0) return null;
        int depth = 0;
        for (int p = br; p < json.Length; p++)
        {
            char c = json[p];
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return json.Substring(br, p - br + 1); }
        }
        return null;
    }

    static bool ReadFull(Stream s, byte[] buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf, n, buf.Length - n); if (r <= 0) return false; n += r; }
        return true;
    }

    public static void Add(HttpWebRequest r, string method, string pathAndQuery, string token)
    {
        r.Headers["Authorization"] = "Bearer " + token;
        r.Headers["X-Overlay-Version"] = "2";
        string ts = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds).ToString();
        r.Headers["X-Overlay-Client"] = "PVN";
        r.Headers["X-Overlay-Ts"] = ts;
        using (var h = new HMACSHA256(Encoding.ASCII.GetBytes(HmacKey)))
        {
            byte[] sig = h.ComputeHash(Encoding.ASCII.GetBytes(ts + "." + method + "." + pathAndQuery));
            var sb = new StringBuilder(sig.Length * 2);
            foreach (byte b in sig) sb.Append(b.ToString("x2"));
            r.Headers["X-Overlay-Sig"] = sb.ToString();
        }
    }
}

// 1 con dino đang đánh nhau với bạn (gộp theo SteamId). Snapshot không đổi -> luồng giao diện đọc được.
class CombatFoe
{
    public string SteamId, Name, Species;
    public double Growth = -1;             // 0..1 (từ attackerGrowth/victimGrowth)
    public double Hp = -1, MaxSeen = -1;   // -1 = chưa biết (chỉ biết khi bạn đánh trúng nó); MaxSeen = hp cao nhất từng thấy -> vẽ thanh
    public DateTime LastHit;
    public double DmgToYou, DmgFromYou;    // tổng sát thương nó gây cho bạn / bạn gây cho nó
    public bool Killed;
}

class PvnCombat
{
    public Func<string> TokenProvider;     // lấy token (PvnLive dùng chung cách giải mã)
    public Func<bool> Enabled;             // có bật tính năng + đang trong game không
    public volatile CombatFoe[] Foes = new CombatFoe[0]; // đối thủ trong trận hiện tại (rỗng = không combat)
    public volatile bool InCombat;
    public volatile string State = "";
    public DateTime CombatStart;
    public volatile int PollMs = 2000;     // nhịp gọi combatlog (nút trong tool đổi 2000 <-> 500)

    const double WindowSec = 10;           // không có đòn mới trong 10 s -> hết combat
    Thread th; volatile bool stop;
    readonly Dictionary<string, CombatFoe> foes = new Dictionary<string, CombatFoe>();
    readonly HashSet<string> seenIds = new HashSet<string>(); // id đòn đã xử lý (không đếm trùng khi poll lại)
    string mySteamId;

    public bool Running { get { return th != null && th.IsAlive; } }

    public void Start(string steamId)
    {
        mySteamId = steamId;
        if (Running) return;
        stop = false;
        th = new Thread(Loop) { IsBackground = true, Name = "PvnCombat" };
        th.Start();
    }

    public void Stop() { stop = true; Clear(); }

    void Clear()
    {
        lock (foes) { foes.Clear(); seenIds.Clear(); }
        Foes = new CombatFoe[0]; InCombat = false;
    }

    void Loop()
    {
        while (!stop)
        {
            bool on = (Enabled == null || Enabled()) && PvnSign.HasKey;
            if (!on) { if (InCombat || Foes.Length > 0) Clear(); Sleep(1000); continue; }
            string token = TokenProvider != null ? TokenProvider() : null;
            if (token == null) { Sleep(2000); continue; }
            try { Poll(token); } catch (Exception e) { State = "lỗi: " + Short(e); }
            Prune();
            Sleep(PollMs < 100 ? 100 : PollMs);
        }
        Clear();
    }

    static string Short(Exception e) { var w = e as WebException; if (w != null && w.Response is HttpWebResponse) return "HTTP " + (int)((HttpWebResponse)w.Response).StatusCode; return e.Message; }

    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !stop; i++) Thread.Sleep(100); }

    void Poll(string token)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var r = (HttpWebRequest)WebRequest.Create("https://islepilot.eu/api/overlay/combatlog");
        r.UserAgent = "DinoReplayReader";
        r.Accept = "application/json";
        r.Timeout = 8000;
        PvnSign.Add(r, "GET", "/api/overlay/combatlog", token);
        string body;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var s = new StreamReader(resp.GetResponseStream()))
            body = s.ReadToEnd();
        State = "ok " + DateTime.Now.ToString("HH:mm:ss");
        Ingest(body);
    }

    // Mỗi hit = 1 object phẳng (không lồng {}), có "attackerSteamId". Các trường: id, at (ISO UTC),
    // attacker/victim + SteamId/Name/Species/Growth, cause, ability, damage, hpBefore, hpAfter, distanceM, killed.
    static readonly Regex HitRx = new Regex("\\{[^{}]*\"attackerSteamId\"[^{}]*\\}", RegexOptions.Compiled);

    void Ingest(string body)
    {
        if (string.IsNullOrEmpty(body)) return;
        foreach (Match m in HitRx.Matches(body))
        {
            string h = m.Value;
            string id = Str(h, "id"); if (id == null) continue;
            lock (foes)
            {
                if (!seenIds.Add(id)) continue; // đòn đã xử lý ở lần poll trước (API trả 50 đòn lịch sử mỗi lần)
                Apply(h);
            }
        }
    }

    void Apply(string h)
    {
        // combatlog trả cả lịch sử nhiều ngày -> chỉ tính đòn mới (trong WindowSec) làm "đang combat"
        DateTime at = ParseAt(Str(h, "at"));
        if (at == DateTime.MinValue || (DateTime.Now - at).TotalSeconds > WindowSec) return;

        string aId = Str(h, "attackerSteamId"), vId = Str(h, "victimSteamId");
        bool iAtk = aId != null && aId == mySteamId;
        bool iVic = vId != null && vId == mySteamId;
        if (!iAtk && !iVic) return; // chỉ đòn liên quan tới bạn

        string foeId = iAtk ? vId : aId;
        if (foeId == null) return;
        string pre = iAtk ? "victim" : "attacker"; // tiền tố của con đối thủ

        CombatFoe f;
        if (!foes.TryGetValue(foeId, out f)) { f = new CombatFoe { SteamId = foeId }; foes[foeId] = f; }
        string nm = Str(h, pre + "Name"); if (!string.IsNullOrEmpty(nm)) f.Name = nm;
        string sp = Str(h, pre + "Species"); if (!string.IsNullOrEmpty(sp)) f.Species = sp;
        double gr = Num(h, pre + "Growth"); if (!double.IsNaN(gr)) f.Growth = gr;
        if (at > f.LastHit) f.LastHit = at;

        double dmg = Num(h, "damage"), hpAfter = Num(h, "hpAfter"), hpBefore = Num(h, "hpBefore");
        if (iAtk) // bạn đánh nó -> hpAfter là máu CỦA NÓ
        {
            if (!double.IsNaN(dmg)) f.DmgFromYou += dmg;
            if (!double.IsNaN(hpAfter)) { f.Hp = hpAfter; if (hpBefore > f.MaxSeen) f.MaxSeen = hpBefore; if (hpAfter > f.MaxSeen) f.MaxSeen = hpAfter; }
            if (h.Contains("\"killed\":true")) f.Killed = true;
        }
        else // nó đánh bạn -> hpAfter là máu của bạn (không học được máu nó)
        {
            if (!double.IsNaN(dmg)) f.DmgToYou += dmg;
        }
    }

    static DateTime ParseAt(string s)
    {
        DateTime d;
        if (s != null && DateTime.TryParse(s, IC, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d))
            return d.ToLocalTime();
        return DateTime.MinValue;
    }

    void Prune()
    {
        var now = DateTime.Now; var live = new List<CombatFoe>();
        lock (foes)
        {
            var dead = new List<string>();
            foreach (var kv in foes)
            {
                if ((now - kv.Value.LastHit).TotalSeconds <= WindowSec) live.Add(kv.Value);
                else dead.Add(kv.Key);
            }
            foreach (var k in dead) foes.Remove(k);
            if (seenIds.Count > 400) seenIds.Clear(); // tránh phình vô hạn
        }
        live.Sort((a, b) => b.LastHit.CompareTo(a.LastHit));
        bool was = InCombat;
        InCombat = live.Count > 0;
        if (InCombat && !was) CombatStart = now;
        Foes = live.ToArray();
    }

    // ---- đọc JSON đơn giản bằng regex ----
    static string Str(string json, string key)
    {
        if (json == null) return null;
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static double Num(string json, string key)
    {
        if (json == null) return double.NaN;
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
        double v; return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, IC, out v) ? v : double.NaN;
    }
}
