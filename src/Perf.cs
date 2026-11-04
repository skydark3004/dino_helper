using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Ghi lại chỗ chậm (tool + trang bản đồ/radar) vào perf_log.txt cạnh exe, để người gặp giật gửi lại file này.
//  - mỗi dòng: giờ (ms) + nguồn + nội dung
//  - GỘP để không spam: mỗi loại (key) ghi ngay lần đầu; các lần sau trong 30 s chỉ đếm, rồi ghi 1 dòng tóm tắt
//    "N lần nữa trong 30 s, nặng nhất: ..." -> máy chậm liên tục cũng chỉ ~1 dòng / 30 s cho mỗi loại
//  - file > 2 MB -> đổi thành perf_log.old.txt, bắt đầu file mới
static class PerfLog
{
    const double WindowS = 30;
    class Win { public DateTime Start; public int N; public double Max = -1; public string MaxMsg; }
    static readonly object lk = new object();
    static readonly Dictionary<string, Win> wins = new Dictionary<string, Win>();
    public static string FilePath { get { return Path.Combine(Program.ExeDir, "perf_log.txt"); } }
    // GUI đăng ký để hiện dòng log trong ô log (gọi từ luồng bất kỳ)
    public static event Action<string> Logged;
    // Chỉ ghi khi người dùng bật trong IsleReplayConfig.bat (mục 5 -> settings.txt: perflog=1). Mặc định tắt.
    // Tắt: Write/Flush không làm gì (không tạo file, không hiện trong ô log; trang gửi /log cũng bị bỏ qua).
    public static volatile bool Enabled;

    // key = loại (null: luôn ghi, dùng cho dòng thông tin hiếm); val = độ nặng (ms, s...) để chọn lần nặng nhất khi gộp
    public static void Write(string key, string msg, double val = 0)
    {
        if (!Enabled) return;
        var lines = new List<string>();
        lock (lk)
        {
            DateTime now = DateTime.Now;
            CollectDue(now, lines);
            Win w;
            if (key == null) lines.Add(msg);
            else if (!wins.TryGetValue(key, out w) || (w.N == 0 && (now - w.Start).TotalSeconds >= WindowS))
            {
                wins[key] = new Win { Start = now };
                lines.Add(msg);
            }
            else
            {
                w.N++;
                if (val >= w.Max) { w.Max = val; w.MaxMsg = msg; }
            }
            Emit(now, lines);
        }
        Notify(lines);
    }

    // bật/tắt (GUI gọi khi settings.txt đổi); tắt -> bỏ các lần đang gộp dở
    public static void SetEnabled(bool on)
    {
        lock (lk) { if (!on) wins.Clear(); Enabled = on; }
    }

    // gọi định kỳ (timer 1 s): ghi dòng tóm tắt cho các loại đã hết 30 s mà còn lần bị gộp
    public static void Flush()
    {
        if (!Enabled) return;
        var lines = new List<string>();
        lock (lk)
        {
            DateTime now = DateTime.Now;
            CollectDue(now, lines);
            Emit(now, lines);
        }
        Notify(lines);
    }

    static void CollectDue(DateTime now, List<string> lines)
    {
        foreach (var w in wins.Values)
        {
            double el = (now - w.Start).TotalSeconds;
            if (w.N > 0 && el >= WindowS)
            {
                lines.Add(string.Format("   ↳ {0} lần nữa trong {1:0} s, nặng nhất: {2}", w.N, el, w.MaxMsg));
                w.Start = now; w.N = 0; w.Max = -1; w.MaxMsg = null; // bắt đầu cửa sổ mới (vẫn gộp nếu còn chậm)
            }
        }
    }

    // lines -> thêm giờ, ghi file (đang giữ lk)
    static void Emit(DateTime now, List<string> lines)
    {
        if (lines.Count == 0) return;
        for (int i = 0; i < lines.Count; i++) lines[i] = now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + lines[i];
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                string old = Path.Combine(Program.ExeDir, "perf_log.old.txt");
                if (File.Exists(old)) File.Delete(old);
                File.Move(FilePath, old);
            }
            File.AppendAllText(FilePath, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
        }
        catch { }
    }

    static void Notify(List<string> lines)
    {
        var h = Logged;
        if (h == null) return;
        foreach (var l in lines) { try { h(l); } catch { } }
    }
}
