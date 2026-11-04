using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Chỗ có sỏi (gastrolith) tự ghi nhận: mỗi khi một khủng long trong replay phát animation "..._Grab_Gastrolith"
// (nhặt sỏi), vị trí của nó lúc đó được lưu vào gastro_learned.txt cạnh exe (dùng chung cho mọi replay sau).
// Dữ liệu sỏi của PVN không khớp với game (đã thử: đứng trên bãi sỏi, điểm PVN gần nhất cách 150 m).
static class GastroStore
{
    const double MergeCm = 1500; // 2 lần nhặt cách nhau < 15 m = cùng 1 chỗ
    static readonly object lk = new object();
    static List<int[]> pts;
    static string cachedJson; static int cachedCount = -1;

    static string FilePath { get { return Path.Combine(Program.ExeDir, "gastro_learned.txt"); } }

    static void Load()
    {
        if (pts != null) return;
        pts = new List<int[]>();
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(',');
                int x, y, z;
                if (p.Length >= 3 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out z)) pts.Add(new[] { x, y, z });
            }
        }
        catch { }
    }

    // pos = "x;y;z" (cm). Trả về true nếu là chỗ mới.
    public static bool Add(string pos, string who)
    {
        if (string.IsNullOrEmpty(pos)) return false;
        var p = pos.Split(';');
        double x, y, z;
        if (p.Length < 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
        lock (lk)
        {
            Load();
            foreach (var q in pts) { double dx = q[0] - x, dy = q[1] - y; if (dx * dx + dy * dy < MergeCm * MergeCm) return false; }
            var n = new[] { (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(z) };
            pts.Add(n);
            try
            {
                if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "# Cho co soi (tu ghi khi thay animation nhat soi trong replay): x,y,z (cm, toa do game), luc ghi, loai khung long\r\n", new UTF8Encoding(false));
                File.AppendAllText(FilePath, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:yyyy-MM-dd HH:mm},{4}\r\n", n[0], n[1], n[2], DateTime.Now, (who ?? "").Replace(",", " ")), new UTF8Encoding(false));
            }
            catch { }
            return true;
        }
    }

    // [[x,y],...] cho bản đồ (cache, chỉ dựng lại khi có điểm mới)
    public static string Json()
    {
        lock (lk)
        {
            Load();
            if (cachedCount == pts.Count) return cachedJson;
            var sb = new StringBuilder("[");
            for (int i = 0; i < pts.Count; i++) sb.Append(i > 0 ? "," : "").Append('[').Append(pts[i][0]).Append(',').Append(pts[i][1]).Append(']');
            cachedJson = sb.Append(']').ToString(); cachedCount = pts.Count;
            return cachedJson;
        }
    }
}
