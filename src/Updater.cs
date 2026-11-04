using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// Tự cập nhật từ GitHub Releases (bản Latest do workflow .github/workflows/release.yml tạo mỗi lần push).
//  - Bản đang dùng: version.txt cạnh exe ("build-N", workflow ghi vào gói). Không có = bản tự build.
//  - Cập nhật: tải DinoHelper.zip -> giải nén vào thư mục tạm -> đổi tên exe đang chạy thành .oldHHmmss.exe
//    (Windows cho đổi tên file exe đang chạy) -> chép file mới đè lên -> mở bản mới (-afterupdate <pid>) và tắt bản này.
//  - Giữ dữ liệu người dùng: map_layers.txt không đụng; gastro_learned.txt được GỘP (thêm chỗ sỏi mới trong gói).
static class Updater
{
    public const string Repo = "skydark3004/dino_helper";
    public const string ZipUrl = "https://github.com/" + Repo + "/releases/latest/download/DinoHelper.zip";
    public const string PageUrl = "https://github.com/" + Repo + "/releases/latest";
    const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    public class Info { public string Tag, Name, Notes, Date; }

    static string VersionFile { get { return Path.Combine(Program.ExeDir, "version.txt"); } }

    public static string CurrentVersion()
    {
        try { if (File.Exists(VersionFile)) { string v = File.ReadAllText(VersionFile).Trim(); if (v.Length > 0) return v; } } catch { }
        return null;
    }

    // "build-12" -> 12; không đọc được -> -1
    public static int BuildNumber(string tag)
    {
        if (tag == null) return -1;
        var m = Regex.Match(tag, @"(\d+)\s*$");
        int n; return m.Success && int.TryParse(m.Groups[1].Value, out n) ? n : -1;
    }

    static HttpWebRequest Req(string url)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 (GitHub bắt buộc)
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.UserAgent = "DinoReplayReader-updater";
        r.Timeout = 20000; r.ReadWriteTimeout = 60000;
        r.AllowAutoRedirect = true;
        return r;
    }

    public static Info CheckLatest()
    {
        var r = Req(ApiUrl); r.Accept = "application/vnd.github+json";
        string json;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();
        return new Info { Tag = JStr(json, "tag_name"), Name = JStr(json, "name"), Notes = JStr(json, "body"), Date = JStr(json, "published_at") };
    }

    // lấy giá trị chuỗi cấp ngoài cùng của JSON (đủ dùng cho vài trường của API releases)
    static string JStr(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return "";
        try { return Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
    }

    // Tải + áp dụng. Trả về đường dẫn exe mới (để mở lại). progress: thông báo tiến trình (gọi từ luồng nền).
    public static string DownloadAndApply(Action<string> progress)
    {
        string dir = Program.ExeDir;
        string tmp = Path.Combine(Path.GetTempPath(), "DinoHelper_update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string zip = Path.Combine(tmp, "DinoHelper.zip");
            var r = Req(ZipUrl);
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = File.Create(zip))
            {
                long total = resp.ContentLength, done = 0; var buf = new byte[81920]; int n; var last = DateTime.MinValue;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n); done += n;
                    if ((DateTime.Now - last).TotalMilliseconds > 300) { last = DateTime.Now; progress(total > 0 ? string.Format("Đang tải… {0:0}%  ({1:0.0} / {2:0.0} MB)", done * 100.0 / total, done / 1048576.0, total / 1048576.0) : string.Format("Đang tải… {0:0.0} MB", done / 1048576.0)); }
                }
            }
            progress("Đang giải nén…");
            string ex = Path.Combine(tmp, "x");
            ZipFile.ExtractToDirectory(zip, ex);
            // gói có thư mục DinoHelper\ bên trong
            string root = File.Exists(Path.Combine(ex, "DinoReplayReader.exe")) ? ex : Path.Combine(ex, "DinoHelper");
            string newExe = Path.Combine(root, "DinoReplayReader.exe");
            if (!File.Exists(newExe)) throw new Exception("Gói tải về không có DinoReplayReader.exe.");

            progress("Đang thay file…");
            string myExe = Path.Combine(dir, "DinoReplayReader.exe");
            // exe đang chạy -> đổi tên để chép bản mới vào đúng tên DinoReplayReader.exe
            if (File.Exists(myExe))
            {
                string old = Path.Combine(dir, "DinoReplayReader.old" + DateTime.Now.ToString("HHmmss") + ".exe");
                File.Move(myExe, old);
            }
            File.Copy(newExe, myExe, true);

            var errors = new List<string>();
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).TrimStart('\\');
                if (rel.Equals("DinoReplayReader.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.Equals("map_layers.txt", StringComparison.OrdinalIgnoreCase)) continue; // cài đặt riêng
                string dst = Path.Combine(dir, rel);
                try
                {
                    if (rel.Equals("gastro_learned.txt", StringComparison.OrdinalIgnoreCase)) { MergeGastro(f); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    // ảnh bản đồ lớn và hầu như không đổi: cùng cỡ thì bỏ qua (đỡ ghi đè file máy chủ đang phục vụ)
                    if (File.Exists(dst) && rel.StartsWith("maps\\", StringComparison.OrdinalIgnoreCase) && new FileInfo(dst).Length == new FileInfo(f).Length) continue;
                    File.Copy(f, dst, true);
                }
                catch (Exception e) { errors.Add(rel + ": " + e.Message); }
            }
            if (errors.Count > 0) progress("Một số file không thay được: " + string.Join("; ", errors));
            return myExe;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // thêm chỗ sỏi trong gói vào gastro_learned.txt của người dùng (bỏ chỗ trùng < 15 m)
    static void MergeGastro(string file)
    {
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(',');
            if (p.Length >= 3) GastroStore.Add(p[0] + ";" + p[1] + ";" + p[2], p.Length >= 5 ? p[4] : "");
        }
    }

    // xoá các bản exe cũ đã đổi tên (gọi lúc mở tool; bản còn chạy thì bỏ qua)
    public static void CleanupOld()
    {
        try { foreach (var f in Directory.GetFiles(Program.ExeDir, "DinoReplayReader.old*.exe")) try { File.Delete(f); } catch { } } catch { }
    }
}
