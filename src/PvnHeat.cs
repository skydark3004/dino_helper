using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Bản đồ nhiệt PVN (pvn.islepilot.eu): API chỉ cần cookie đăng nhập "islepilot_player" (JWT, hạn 30 ngày).
// Cookie là HttpOnly (JavaScript không đọc được) -> người dùng chép tay trong DevTools (Application > Cookies, hoặc Copy as cURL) rồi dán vào tool.
// Cookie lưu ở pvn_cookie.dat cạnh exe, mã hoá DPAPI theo tài khoản Windows (máy khác / người khác không đọc được).
static class PvnHeat
{
    public const string ApiUrl = "https://pvn.islepilot.eu/api/p/pvn/map/heatmap";
    public const string PageUrl = "https://pvn.islepilot.eu/map";
    static string CookieFile { get { return Path.Combine(Program.ExeDir, "pvn_cookie.dat"); } }

    // Lấy JWT từ chữ người dùng dán (chỉ token, "islepilot_player=...", hoặc cả chuỗi cookie)
    public static string ExtractToken(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"islepilot_player=(eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(text, @"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+");
        if (!m.Success) return null;
        var info = Decode(m.Value);
        return info != null && info.SteamId != null ? m.Value : null; // JWT khác (không phải của PVN) -> bỏ
    }

    public class Info { public string Name, SteamId; public DateTime Expires; }

    // Đọc phần payload của JWT (không kiểm tra chữ ký – chỉ để hiện tên + ngày hết hạn)
    public static Info Decode(string token)
    {
        try
        {
            string p = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            while (p.Length % 4 != 0) p += "=";
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(p));
            var r = new Info();
            var m = Regex.Match(json, "\"steamId\"\\s*:\\s*\"(\\d+)\""); if (m.Success) r.SteamId = m.Groups[1].Value;
            m = Regex.Match(json, "\"personaName\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""); if (m.Success) r.Name = Regex.Unescape(m.Groups[1].Value);
            m = Regex.Match(json, "\"exp\"\\s*:\\s*(\\d+)");
            r.Expires = m.Success ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(long.Parse(m.Groups[1].Value)).ToLocalTime() : DateTime.MaxValue;
            return r;
        }
        catch { return null; }
    }

    public static void Save(string token)
    {
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CookieFile, enc);
    }

    public static string Load()
    {
        try
        {
            if (!File.Exists(CookieFile)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(CookieFile), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    public static void Clear() { try { File.Delete(CookieFile); } catch { } }

    public enum Result { Ok, AuthFailed, Error }

    // Gọi API. Ok -> json = nội dung trả về ({"ok":true,"cells":[{u,v,intensity}…],"radius":…}), cells = số ô
    public static Result Fetch(string token, out string json, out int cells, out string err)
    {
        json = null; cells = 0; err = null;
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var r = (HttpWebRequest)WebRequest.Create(ApiUrl);
            r.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DinoReplayReader";
            r.Accept = "application/json";
            r.Referer = PageUrl;
            r.Headers["Cookie"] = "islepilot_player=" + token;
            r.Timeout = r.ReadWriteTimeout = 8000;
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = sr.ReadToEnd();
        }
        catch (WebException e)
        {
            var hr = e.Response as HttpWebResponse;
            if (hr != null && ((int)hr.StatusCode == 401 || (int)hr.StatusCode == 403)) { err = "máy chủ PVN từ chối (" + (int)hr.StatusCode + ")"; return Result.AuthFailed; }
            err = hr != null ? "máy chủ PVN trả lỗi " + (int)hr.StatusCode : e.Message;
            return Result.Error;
        }
        catch (Exception e) { err = e.Message; return Result.Error; }

        json = json.Trim();
        if (Regex.IsMatch(json, "\"ok\"\\s*:\\s*false")) { err = "cookie hết hạn hoặc không đúng"; return Result.AuthFailed; }
        if (!json.StartsWith("{") || !Regex.IsMatch(json, "\"ok\"\\s*:\\s*true")) { err = "dữ liệu lạ từ PVN: " + (json.Length > 80 ? json.Substring(0, 80) : json); return Result.Error; }
        cells = Regex.Matches(json, "\"u\"\\s*:").Count;
        return Result.Ok;
    }
}
