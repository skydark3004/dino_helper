using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

partial class Replay
{
    public Dictionary<string, int> ChNameCount = new Dictionary<string, int>();
    public bool Stage1 = false;

    partial void OnBunchImpl(Bunch u);

    void OnBunch(Bunch u)
    {
        if (u.ChName != null) { int c; ChNameCount.TryGetValue(u.ChName, out c); ChNameCount[u.ChName] = c + 1; }
        if (!Stage1) OnBunchImpl(u);
    }
}

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    public static string ExeDir { get { return Path.GetDirectoryName(typeof(Program).Assembly.Location); } }
    public static string DefaultOutDir(string file) { return Path.Combine(ExeDir, "output", Path.GetFileNameWithoutExtension(file)); }

    // Trích xuất 1 file replay -> thư mục kết quả. Trả về đường dẫn thư mục.
    public static Replay NewReplay(string file, bool full, TextWriter log)
    {
        return new Replay { Log = log, FullOutput = full, OutDir = DefaultOutDir(file) };
    }

    public static string Extract(string file, bool full, TextWriter log, Action<Replay> configure = null)
    {
        return ExtractReplay(file, full, log, configure).OutDir;
    }

    // Như Extract nhưng trả về bộ đọc (giữ trong bộ nhớ để tự cập nhật nối tiếp khi file còn đang quay)
    public static Replay ExtractReplay(string file, bool full, TextWriter log, Action<Replay> configure = null)
    {
        var rp = NewReplay(file, full, log);
        if (configure != null) configure(rp);
        try { rp.ParseFile(file); }
        catch (Exception e) { log.WriteLine("LỖI khi đọc: " + e.Message); }
        log.WriteLine("frames {0} packets {1} ok {2} err {3}", rp.FrameCount, rp.PacketCount, rp.PacketsOk, rp.PacketErrors);
        rp.Finish();
        rp.Log = TextWriter.Null; // các lần cập nhật sau không ghi log chi tiết
        return rp;
    }

    [STAThread]
    static int Main(string[] args)
    {
        // vừa tự cập nhật: đợi bản cũ tắt hẳn (nhả cổng máy chủ bản đồ, phím tắt) rồi mở giao diện như bình thường
        if (args.Length == 2 && args[0] == "-afterupdate")
        {
            int pid;
            if (int.TryParse(args[1], out pid))
                try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { }
            args = new string[0];
        }
        // không có tham số -> mở giao diện
        if (args.Length == 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        // chạy dòng lệnh (DocNhanh.bat / DocDayDu.bat): gắn vào cửa sổ console đang mở
        AttachConsole(-1);
        string file = args[0];
        bool full = false;
        Action<Replay> cfg = rp =>
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-max") rp.MaxPacketBits = uint.Parse(args[++i]);
                else if (args[i] == "-pce") rp.PartialCustomExportsBit = true;
                else if (args[i] == "-s1") rp.Stage1 = true;
                else if (args[i] == "-groups") rp.DumpGroups = true;
                else if (args[i] == "-pcb") rp.PropChecksumBit = true;
                else if (args[i] == "-nodestroy") rp.SubDestroyBit = false;
                else if (args[i] == "-noouter") rp.SubOuterBit = false;
                else if (args[i] == "-out") rp.OutDir = args[++i];
            }
        };
        if (args.Contains("-chunks")) { ReplayCut.ListChunks(file, Console.Out); return 0; }
        // -merge [-o <file mới>]: gộp chunk nhỏ thành chunk ~20 s (bản để chia sẻ / xem mượt trên máy khác)
        if (args.Contains("-merge"))
        {
            int oi = Array.IndexOf(args, "-o");
            string outFile = oi > 0 ? args[oi + 1] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFileNameWithoutExtension(file) + "_gop.replay");
            if (Path.GetFullPath(outFile).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("LỖI: file mới trùng file gốc"); return 1; }
            try { Console.WriteLine(ReplayCut.Merge(file, outFile, 20000)); }
            catch (Exception e) { Console.WriteLine("LỖI: " + e.Message); return 1; }
            Console.WriteLine("-> " + outFile);
            return 0;
        }
        if (args.Contains("-bunchstats")) { ReplayCut.BunchStats(file, Console.Out); return 0; }
        if (args.Length > 2 && args[1] == "-frames") { ReplayCut.DumpFrames(file, int.Parse(args[2]), Console.Out); return 0; }
        if (args.Length > 2 && args[1] == "-cpdump") { ReplayCut.DumpCheckpoint(file, int.Parse(args[2]), Console.Out); return 0; }
        // -cut <từ phút> [đến phút] [-keeptime] -o <file mới>
        int ci = Array.IndexOf(args, "-cut");
        if (ci > 0)
        {
            // thời gian: 52 | 52:30 | 1:02:30
            long fromMs, toMs = 0;
            try
            {
                fromMs = ReplayCut.ParseTime(args[ci + 1]);
                if (ci + 2 < args.Length && !args[ci + 2].StartsWith("-")) toMs = Math.Max(0, ReplayCut.ParseTime(args[ci + 2]));
            }
            catch (FormatException e) { Console.WriteLine("LỖI: thời gian không hợp lệ: " + e.Message + " (vd 52, 52:30, 1:05:00)"); return 1; }
            int oi = Array.IndexOf(args, "-o");
            string outFile = oi > 0 ? args[oi + 1] : ReplayCut.DefaultCutName(file, fromMs, toMs);
            if (Path.GetFullPath(outFile).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("LỖI: file mới trùng file gốc"); return 1; }
            try { Console.WriteLine(ReplayCut.Cut(file, fromMs, toMs, !args.Contains("-keeptime"), outFile, Console.Out, args.Contains("-nomerge") ? 0u : 20000u)); }
            catch (Exception e) { Console.WriteLine("LỖI: " + e.Message); return 1; }
            Console.WriteLine("-> " + outFile);
            return 0;
        }
        full = args.Contains("-full");
        int ai = Array.IndexOf(args, "-alerts");
        if (ai > 0) { SimulateAlerts(file, int.Parse(args[ai + 1])); return 0; }
        int si = Array.IndexOf(args, "-simulate");
        if (si > 0) { Simulate(file, int.Parse(args[si + 1]), args[Array.IndexOf(args, "-out") + 1]); return 0; }
        Extract(file, full, Console.Out, cfg);
        return 0;
    }

    // Thử nghiệm báo "khủng long mới vào vùng": đọc dần file như khi game đang quay, "giờ hiện tại" = giờ trong replay
    static void SimulateAlerts(string file, int step)
    {
        byte[] all = File.ReadAllBytes(file);
        string tmp = Path.Combine(Path.GetTempPath(), "isle_alert_" + Guid.NewGuid().ToString("N") + ".replay");
        var rp = new Replay { Log = TextWriter.Null, OutDir = Path.GetTempPath() };
        var present = new Dictionary<string, DateTime>();
        int total = 0;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                for (int n = 0; n < all.Length; )
                {
                    int k = Math.Min(step, all.Length - n);
                    fs.Write(all, n, k); fs.Flush(); n += k;
                    try { rp.LoadFile(tmp); rp.ParseMore(); } catch (EndOfStreamException) { } catch (ArgumentException) { }
                    foreach (var a in rp.CheckNewDinos(present, DateTime.MinValue.AddSeconds(rp.CurTime), TimeSpan.FromSeconds(30)))
                    {
                        Console.WriteLine("{0,8} {1} – {2}", TimeSpan.FromSeconds(a.ReplayTime).ToString(@"h\:mm\:ss"), a.Title, a.Detail);
                        total++;
                    }
                }
        }
        finally { File.Delete(tmp); }
        Console.WriteLine("tổng {0} thông báo", total);
    }

    // Thử nghiệm: giả lập game đang quay bằng cách ghi dần file ra file tạm (mỗi lần `step` byte),
    // đọc nối tiếp sau mỗi lần ghi, cuối cùng xuất kết quả để so với đọc cả file một lần.
    static void Simulate(string file, int step, string outDir)
    {
        byte[] all = File.ReadAllBytes(file);
        string tmp = Path.Combine(Path.GetTempPath(), "isle_sim_" + Guid.NewGuid().ToString("N") + ".replay");
        var rp = new Replay { Log = TextWriter.Null, OutDir = outDir, FullOutput = true };
        int updates = 0; long maxMs = 0; var total = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int n = Math.Min(step, all.Length); ; n = Math.Min(n + step, all.Length))
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) fs.Write(all, 0, n);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { rp.LoadFile(tmp); rp.ParseMore(); } catch (EndOfStreamException) { } catch (ArgumentException) { }
                maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds); updates++;
                if (n == all.Length) break;
            }
        }
        finally { File.Delete(tmp); }
        rp.FullOutput = true;
        rp.Finish();
        File.WriteAllText(Path.Combine(outDir, "sim.txt"), string.Format("updates {0} total {1}ms max-per-update {2}ms frames {3} packets {4} events {5}",
            updates, total.ElapsedMilliseconds, maxMs, rp.FrameCount, rp.PacketCount, rp.Events.Count));
    }
}
