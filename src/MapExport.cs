using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

partial class Replay
{
    // Phân loại đối tượng để tô màu trên bản đồ
    static string Category(Actor a, Actor me)
    {
        // chỉ giữ các lớp cần thiết (bạn, người chơi khác, xác, cá, thịt) cho bản đồ nhẹ
        if (a == me) return "me";
        // loại theo class đã tính sẵn (ClassifyNew): khủng long / cá / thú AI / thịt
        switch (a.Kind)
        {
            // khủng long: người chơi (có SteamId) hoặc AI ("dino"); chết lúc đang thấy -> giữ lớp sống, trang tự đổi thành xác
            // từ giờ chết (trường "d"); đã chết ngay từ lúc thấy lần đầu -> xác luôn
            case KindDino:
                // Compsognathus không có SteamId = AI rác của game -> không đưa lên bản đồ/radar
                if (a.Get("SteamId") == "" && (Species(a) ?? "").IndexOf("Compsognathus", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                return a.Get("bIsDead") == "1" && !DiedWhileSeen(a) ? "corpse" : (a.Get("SteamId") != "" ? "player" : "dino");
            case KindFish: return "fish";
            // thú AI (hươu, lợn rừng, thỏ, gà, cua, dê...): /Game/TheIsle/Core/Characters/Animals/<Loài>/BP_<Loài>
            case KindAnimal: return a.Get("bIsDead") == "1" && !DiedWhileSeen(a) ? "corpse" : "animal";
            case KindPickable: return "meat";
        }
        return null;
    }

    // chết khi đang được theo dõi (giờ chết cách lúc xuất hiện > 0,5 s) -> trước đó là con sống
    static bool DiedWhileSeen(Actor a) { return a.DeadTime > a.OpenTime + 0.5; }

    static string JsStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s ?? "")
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32 || ch == '<' || ch == '>' || ch == '&') sb.AppendFormat("\\u{0:x4}", (int)ch);
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    public string LastMapJson;

    // writeFiles = false: chỉ dựng JSON (để đẩy qua máy chủ), không ghi data.js/version.js ra đĩa
    public void WriteMap(Actor me, Func<Actor, string> owner, bool writeFiles = true)
    {
        string tpl = Path.Combine(Program.ExeDir, "map_template.html");
        if (!File.Exists(tpl)) return;
        string version = DateTime.Now.Ticks.ToString(); // mỗi lần ghi một mã mới
        var sb = new StringBuilder();
        sb.Append("{\"start\":").Append(JsStr(StartTime.ToString("yyyy-MM-dd HH:mm:ss")))
          .Append(",\"length\":").Append(LengthSec.ToString("0.0", IC))
          // thời điểm frame cuối đã đọc (dữ liệu thật sự có tới đây) -> đồng hồ trực tiếp của bản đồ không chạy vượt
          .Append(",\"tmax\":").Append(MaxTime.ToString("0.00", IC))
          .Append(",\"file\":").Append(JsStr(Path.GetFileName(OutDir.TrimEnd('\\'))))
          .Append(",\"map\":").Append(JsStr(MapName))
          .Append(",\"live\":").Append(IsLive ? "true" : "false")
          .Append(",\"updated\":").Append(JsStr(DateTime.Now.ToString("HH:mm:ss")))
          .Append(",\"ver\":").Append(JsStr(version))
          .Append(",\"gastro\":").Append(GastroStore.Json())
          .Append(",\"actors\":[");
        bool first = true;
        ClassifyNew(); // chỉ duyệt actor có thể lên bản đồ (khủng long, cá, thú, thịt), không phải cả ~4.000 actor
        foreach (int ai in mapActorIdx)
        {
            var a = AllActors[ai];
            if (a.Track.Count == 0) continue;
            string cat = Category(a, me);
            if (cat == null) continue;
            string player = a.Kind == KindDino ? owner(a) : "";
            if (!first) sb.Append(',');
            first = false;
            // JSON của đối tượng chỉ dựng lại khi nó có dữ liệu mới (Ver đổi) -> mỗi lần cập nhật trực tiếp rất nhẹ
            if (a.JsonVer != a.Ver || a.JsonCat != cat || a.JsonPlayer != player || a.JsonClose != a.CloseTime)
            {
                string label = Species(a) ?? ShortNameS(a.Class ?? "?");
                if (label.StartsWith("BP_")) label = label.Substring(3);
                if (label.EndsWith("_C")) label = label.Substring(0, label.Length - 2);
                a.JsonCache = ActorJson(a, ai, cat, label, player);
                a.JsonVer = a.Ver; a.JsonCat = cat; a.JsonPlayer = player; a.JsonClose = a.CloseTime;
            }
            sb.Append(a.JsonCache);
        }
        sb.Append("]}");
        LastMapJson = sb.ToString(); // để tool đẩy thẳng vào bản đồ đang mở qua http://
        if (!writeFiles) return;
        // dữ liệu tách riêng ra data.js; ghi ra file tạm rồi đổi tên để trình duyệt không đọc phải file ghi dở
        string dataPath = Path.Combine(OutDir, "data.js");
        WriteAtomic(dataPath, "window.REPLAY_DATA = " + LastMapJson + ";\n");
        // version.js rất nhỏ: bản đồ kiểm tra file này liên tục, chỉ nạp data.js khi version đổi
        WriteAtomic(Path.Combine(OutDir, "version.js"), "window.REPLAY_VERSION = " + JsStr(version) + ";\n");
        string mapPath = Path.Combine(OutDir, "map.html");
        if (!File.Exists(mapPath) || File.GetLastWriteTimeUtc(mapPath) < File.GetLastWriteTimeUtc(tpl)) File.Copy(tpl, mapPath, true);
    }

    static string ActorJson(Actor a, int ai, string cat, string label, string player)
    {
        var sb = new StringBuilder();
        // "i": mã cố định của đối tượng (thứ tự trong replay) -> giữ được mục tiêu đang ghim khi bản đồ tự cập nhật
        sb.Append("{\"i\":").Append(ai).Append(",\"c\":").Append(JsStr(cat)).Append(",\"n\":").Append(JsStr(label));
        if (player != "") sb.Append(",\"p\":").Append(JsStr(player));
        if (a.Get("Growth") != "") sb.Append(",\"g\":").Append(a.Get("Growth"));
        if (a.Get("bIsFemale") == "1") sb.Append(",\"f\":1");
        sb.Append(",\"o\":").Append(a.OpenTime.ToString("0.0", IC));
        if (a.CloseTime >= 0) sb.Append(",\"e\":").Append(a.CloseTime.ToString("0.0", IC));
        // giờ chết (chỉ khi chết lúc đang thấy): trang tách thành "con sống" tới giờ này + "xác" từ giờ này
        if (cat != "corpse" && DiedWhileSeen(a)) sb.Append(",\"d\":").Append(a.DeadTime.ToString("0.0", IC));
        // lịch sử chế độ di chuyển [[giờ, mode],...] (4 = đang bơi) – chỉ vài mục mỗi con
        if (a.ModeHist.Count > 0)
        {
            sb.Append(",\"mm\":[");
            for (int k = 0; k < a.ModeHist.Count; k++)
                sb.Append(k > 0 ? "," : "").AppendFormat(IC, "[{0:0.00},{1:0}]", a.ModeHist[k][0], a.ModeHist[k][1]);
            sb.Append(']');
        }
        sb.Append(",\"t\":[");
        // đường đi: chỉ nối thêm các điểm mới vào phần đã dựng (đường đi chỉ dài thêm, không đổi điểm cũ)
        if (a.TrackCat != cat) { a.TrackJson = new StringBuilder(); a.TrackDone = 0; a.TrackPrev = null; a.TrackCat = cat; }
        for (int k = a.TrackDone; k < a.Track.Count; k++)
        {
            var s = a.Track[k]; var prev = a.TrackPrev;
            // chỉ lưu khi đã di chuyển đủ xa so với mẫu đã lưu trước (cá/thịt 1 m, khủng long 0.3 m)
            // khủng long người chơi: lưu cả khi chỉ xoay người (yaw đổi >= 10°) để chỉ hướng "theo đồng hồ" đúng
            bool withYaw = (cat == "me" || cat == "player") && s.Length > 4;
            double minMove = cat == "fish" || cat == "meat" ? 100 : 30;
            bool turned = withYaw && prev != null && (prev.Length <= 4 /* mẫu trước chưa có hướng */ || Math.Abs(((s[4] - prev[4]) % 360 + 540) % 360 - 180) >= 10);
            if (prev != null && !turned && Math.Abs(prev[1] - s[1]) < minMove && Math.Abs(prev[2] - s[2]) < minMove && Math.Abs(prev[3] - s[3]) < minMove) continue;
            a.TrackPrev = s;
            if (a.TrackJson.Length > 0) a.TrackJson.Append(',');
            if (withYaw) a.TrackJson.AppendFormat(IC, "[{0:0.0},{1:0},{2:0},{3:0},{4:0}]", s[0], s[1], s[2], s[3], s[4]);
            else a.TrackJson.AppendFormat(IC, "[{0:0.0},{1:0},{2:0},{3:0}]", s[0], s[1], s[2], s[3]);
        }
        a.TrackDone = a.Track.Count;
        sb.Append(a.TrackJson).Append("]}");
        return sb.ToString();
    }

    // Ghi ra file tạm rồi thay thế -> trình duyệt không bao giờ đọc phải file ghi dở
    static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}
