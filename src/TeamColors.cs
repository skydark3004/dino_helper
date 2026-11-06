using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Nhận diện TEAM theo định dạng tên người chơi: "ArB | Baby REX", "[ABC] Bob", "ABC - Bob", "ABC_Bob", "ABC Bob" (tag viết hoa),
// "Bob | ABC"... Chỉ coi là team khi từ 2 người trở lên TRONG VÙNG đang xét có cùng tag -> cùng 1 màu (chấm + nhãn).
// Logic y hệt `teamTag` / `teamAssign` trong map_template.html (cùng bảng màu, cùng cách băm) để la bàn và bản đồ ra cùng màu.
static class TeamColors
{
    static readonly string[] Hex = { "#4db8ff", "#b388ff", "#ff6ec7", "#00e5ff", "#7c8cff", "#e040fb", "#ffd740", "#e0e0e0" };
    public static readonly Color[] Palette = BuildPalette();
    static Color[] BuildPalette()
    {
        var a = new Color[Hex.Length];
        for (int i = 0; i < Hex.Length; i++)
            a[i] = Color.FromArgb(int.Parse(Hex[i].Substring(1, 2), NumberStyles.HexNumber), int.Parse(Hex[i].Substring(3, 2), NumberStyles.HexNumber), int.Parse(Hex[i].Substring(5, 2), NumberStyles.HexNumber));
        return a;
    }

    // thử lần lượt: [tag] ở đầu, tag + ký tự ngăn (| • : / - _ .) ở đầu, tag VIẾT HOA + dấu cách, tag ở cuối sau dấu ngăn
    static readonly Regex[] Rx =
    {
        new Regex("^[\\[\\(\\{<【「『]\\s*([^\\]\\)\\}>】」』]{1,10}?)\\s*[\\]\\)\\}>】」』]", RegexOptions.Compiled),
        new Regex("^([^\\s|｜•·:：/\\\\~\\-–—_.]{1,8})\\s*[|｜•·:：/\\\\~\\-–—_.]+\\s*\\S", RegexOptions.Compiled),
        new Regex("^([A-Z0-9]{2,5})\\s+\\S", RegexOptions.Compiled),
        new Regex("^([A-Z0-9]{2,5})[^\\p{L}\\p{N}\\s]", RegexOptions.Compiled),     // DBZ🟡 Ông Việt, UCS◉Hunter: tag viết hoa dính ký hiệu / emoji
        new Regex("^.+?\\s[|｜•·\\-–—]\\s*([^\\s|｜•·\\-–—]{1,8})$", RegexOptions.Compiled),
    };

    // các tag ỨNG VIÊN của 1 tên (đã chuẩn hoá: chữ thường, bỏ ký tự lạ): tag ở đầu và/hoặc ở cuối ("ArB | Bob" -> arb; "Bob | KOF" -> bob, kof)
    public static List<string> Candidates(string name)
    {
        var res = new List<string>();
        if (string.IsNullOrEmpty(name)) return res;
        string s = name;
        try { s = name.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { }   // NFKC: 𝓗𝓣 -> HT (giống JS)
        s = s.Trim();
        if (s.Length == 0) return res;
        foreach (var rx in Rx)
        {
            var m = rx.Match(s);
            if (!m.Success) continue;
            var sb = new StringBuilder();
            foreach (char ch in m.Groups[1].Value.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            if (sb.Length >= 2 && !res.Contains(sb.ToString())) res.Add(sb.ToString());
        }
        return res;
    }

    // tên -> màu team. Mỗi tên chọn tag ứng viên có NHIỀU người dùng chung nhất (phải >= 2 người trong danh sách); tên không có tag chung thì không có màu.
    // Màu theo băm của tag (ổn định), trùng thì lấy màu kế tiếp còn trống.
    public static Dictionary<string, Color> Assign(IEnumerable<string> names)
    {
        var uniq = new List<string>(); var seen = new HashSet<string>();
        foreach (var n in names) if (!string.IsNullOrEmpty(n) && seen.Add(n)) uniq.Add(n);
        var cand = new Dictionary<string, List<string>>(); var cnt = new Dictionary<string, int>();
        foreach (var n in uniq)
        {
            var l = Candidates(n); cand[n] = l;
            foreach (var k in l) { int v; cnt.TryGetValue(k, out v); cnt[k] = v + 1; }
        }
        var keys = new List<string>();
        foreach (var kv in cnt) if (kv.Value >= 2) keys.Add(kv.Key);
        keys.Sort(StringComparer.Ordinal);
        var used = new HashSet<int>(); var colorOfTag = new Dictionary<string, Color>();
        int N = Palette.Length;
        foreach (var k in keys)
        {
            uint h = 2166136261;
            foreach (char ch in k) h = unchecked((h ^ ch) * 16777619u);
            int i = (int)(h % (uint)N);
            for (int j = 0; j < N && used.Contains(i); j++) i = (i + 1) % N;
            used.Add(i);
            colorOfTag[k] = Palette[i];
        }
        var res = new Dictionary<string, Color>();
        foreach (var n in uniq)
        {
            string best = null; int bc = 1;
            foreach (var k in cand[n]) { int v = cnt[k]; if (v > bc) { bc = v; best = k; } }
            if (best != null) res[n] = colorOfTag[best];
        }
        return res;
    }
}
