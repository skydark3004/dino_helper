using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

partial class Replay
{
    public DateTime StartTime; public double LengthSec; public string MapName = "";

    static string Species(Actor a)
    {
        if (a.Class == null || !a.Class.Contains("/Characters/Dinosaurs/")) return null;
        string s = ShortNameS(a.Class);
        if (!s.StartsWith("BP_") || !s.EndsWith("_C") || s.Contains("Burrow")) return null;
        return s.Substring(3, s.Length - 5);
    }
    static string ShortNameS(string path)
    {
        string s = path.Substring(path.LastIndexOfAny(new[] { '.', '/' }) + 1);
        if (s.StartsWith("Default__")) s = s.Substring(9);
        return s;
    }
    static string Unq(string s) { return s.Trim('"'); }
    static string Pos(string mov) { if (string.IsNullOrEmpty(mov)) return "?"; var p = mov.Split(';'); return "X " + p[0] + "  Y " + p[1] + "  Z " + p[2]; }
    static string Pct(string f) { double d; return double.TryParse(f, NumberStyles.Float, IC, out d) ? (d * 100).ToString("0.#", IC) + "%" : "?"; }
    static string Sec(double t) { return TimeSpan.FromSeconds(t).ToString(@"m\:ss"); }

    // Hướng trên bản đồ The Isle (đã kiểm chứng: đi về Nam thì Y tăng): Bắc = -Y, Đông = +X
    static readonly string[] Compass = { "Bắc", "Đông Bắc", "Đông", "Đông Nam", "Nam", "Tây Nam", "Tây", "Tây Bắc" };
    static string CompassName(double deg) { return Compass[(int)Math.Round(((deg % 360) + 360) % 360 / 45) % 8]; }
    // hướng từ a tới b, 0 = Bắc, 90 = Đông
    static double Bearing(double[] a, double[] b)
    {
        double east = b[1] - a[1], north = -(b[2] - a[2]);
        return Math.Atan2(east, north) * 180 / Math.PI;
    }
    // yaw của Unreal (0 = +X = Đông, 90 = +Y = Nam) -> la bàn (0 = Bắc)
    static double YawToCompass(double yaw) { return yaw + 90; }

    // khoảng cách 3D theo mét (đơn vị Unreal = cm)
    static double Dist(double[] a, double[] b)
    {
        return Math.Sqrt(Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2) + Math.Pow(a[3] - b[3], 2)) / 100;
    }
    static double? DistNow(Actor me, Actor d)
    {
        if (me.Track.Count == 0 || d.Track.Count == 0) return null;
        return Dist(me.Track.Last(), d.Track.Last());
    }
    // khoảng cách nhỏ nhất trong suốt replay, xét tại mọi thời điểm một trong hai con có cập nhật vị trí
    static double? Closest(Actor me, Actor d, out double at)
    {
        at = 0; double? best = null;
        foreach (double t in me.Track.Select(s => s[0]).Concat(d.Track.Select(s => s[0])).Distinct())
        {
            if (d.CloseTime >= 0 && t > d.CloseTime) continue;
            double[] a = me.PosAt(t), b = d.PosAt(t);
            if (a == null || b == null) continue;
            double x = Dist(a, b);
            if (best == null || x < best) { best = x; at = t; }
        }
        return best;
    }

    Dictionary<uint, Actor> PlayerStates()
    {
        var psByGuid = new Dictionary<uint, Actor>();
        foreach (var a in AllActors.Where(x => x.Class != null && x.Class.Contains("PlayerState") && x.Get("PlayerNamePrivate") != ""))
            psByGuid[a.Guid] = a;
        return psByGuid;
    }
    // tên người chơi điều khiển một con khủng long (qua PlayerState)
    static Func<Actor, string> MakeOwner(Dictionary<uint, Actor> psByGuid)
    {
        return d =>
        {
            string g = d.Get("PlayerState"); uint id; Actor ps;
            if (g.StartsWith("guid:") && uint.TryParse(g.Substring(5), out id) && psByGuid.TryGetValue(id, out ps)) return Unq(ps.Get("PlayerNamePrivate"));
            return "";
        };
    }
    // khủng long của bạn = con có Controller (chỉ gửi cho chính chủ)
    Actor FindMe() { return AllActors.FirstOrDefault(d => Species(d) != null && d.Get("Controller") != ""); }

    // Khi đang quay: chỉ ghi dữ liệu bản đồ (data.js + version.js), rất nhanh
    public void WriteLiveMap(bool writeFiles = true)
    {
        Directory.CreateDirectory(OutDir);
        WriteMap(FindMe(), MakeOwner(PlayerStates()), writeFiles);
    }

    void WriteReport()
    {
        var sb = new StringBuilder();
        Func<string, Actor> byClass = c => AllActors.FirstOrDefault(a => a.Class != null && a.Class.Contains(c));
        sb.AppendLine("BÁO CÁO REPLAY DINO (trích xuất tự động - không chính xác 100%)");
        sb.AppendLine("====================================================================");
        sb.AppendFormat("Bắt đầu quay : {0:yyyy-MM-dd HH:mm:ss}\r\n", StartTime);
        sb.AppendFormat("Độ dài       : {0}\r\n", Sec(LengthSec));
        sb.AppendFormat("Bản đồ       : {0}\r\n", MapName);

        var gs = byClass("SurvivalGameState");
        if (gs != null)
        {
            sb.AppendLine();
            sb.AppendLine("--- SERVER ---");
            sb.AppendFormat("Số người chơi : {0} (lúc đầu {1})\r\n", gs.Get("ServerPlayerAmount"), gs.GetFirst("ServerPlayerAmount"));
            sb.AppendFormat("AI còn sống   : {0} → {1}\r\n", gs.GetFirst("AIAlive"), gs.Get("AIAlive"));
            sb.AppendFormat("Server FPS    : {0}\r\n", gs.Get("ServerFPS"));
            double wt; if (double.TryParse(gs.GetFirst("ReplicatedWorldTimeSecondsDouble"), NumberStyles.Float, IC, out wt))
                sb.AppendFormat("Server đã chạy: {0:0.0} giờ\r\n", wt / 3600);
        }
        var sky = byClass("Ultra_Dynamic_Sky");
        if (sky != null)
        {
            Func<string, string> tod = s => { double d; if (!double.TryParse(s, NumberStyles.Float, IC, out d)) return "?"; int h = (int)(d / 100); int m = (int)((d / 100 - h) * 60); return string.Format("{0:00}:{1:00}", h, m); };
            sb.AppendFormat("Giờ trong game: {0} → {1} (ngày {2}, tháng {3})\r\n", tod(sky.GetFirst("TimeOfDayIsle")), tod(sky.Get("TimeOfDayIsle")), sky.Get("Day"), sky.Get("Month"));
        }

        // Người chơi
        var psByGuid = PlayerStates();
        Func<Actor, string> owner = MakeOwner(psByGuid);

        // Khủng long
        var dinos = AllActors.Where(a => Species(a) != null).ToList();
        var me = FindMe();
        // khoảng cách hiện tại (vị trí cuối cùng biết được) -> sắp xếp gần trước
        dinos = dinos.OrderBy(d => d == me ? -1 : (me == null ? 0 : DistNow(me, d) ?? double.MaxValue)).ToList();
        sb.AppendLine();
        sb.AppendLine("--- KHỦNG LONG TRONG TẦM NHÌN (gần bạn nhất trước) ---");
        var csv = new List<string> { "channel,species,status,player,steam_id,gender,growth,x,y,z,distance_m,direction,closest_m,closest_at,first_seen,last_seen,attr0,attr4,attr20,attr22,attr24" };
        foreach (var d in dinos)
        {
            string distNow = "", closest = "", closestAt = "", dirNow = "";
            if (me != null && d != me)
            {
                double? dn = DistNow(me, d);
                double ct; double? dc = Closest(me, d, out ct);
                if (dn != null) distNow = dn.Value.ToString("0", IC);
                if (dc != null) { closest = dc.Value.ToString("0", IC); closestAt = Sec(ct); }
            }
            bool mine = d.Get("Controller") != "";
            bool dead = d.Get("bIsDead") == "1";
            string name = owner(d);
            string status = dead ? "XÁC" : (name != "" || d.Get("SteamId") != "" ? "người chơi" : "AI/khác");
            string gender = d.Get("bIsFemale") == "1" ? "cái" : (d.Get("SteamId") != "" ? "đực" : "?");
            string lp = d.LastPos ?? d.GetFirst("Spawn");
            sb.AppendFormat("{0}{1} [{2}]{3}\r\n", mine ? "★ " : "• ", Species(d), status, mine ? "  <-- KHỦNG LONG CỦA BẠN" : "");
            if (name != "") sb.AppendFormat("    Người chơi : {0}\r\n", name);
            if (d.Get("SteamId") != "") sb.AppendFormat("    SteamID    : {0}\r\n", Unq(d.Get("SteamId")));
            if (!dead && d.Get("SteamId") != "") sb.AppendFormat("    Giới tính  : {0}\r\n", gender);
            if (d.Get("Growth") != "") sb.AppendFormat("    Trưởng thành: {0} (lúc đầu {1})\r\n", Pct(d.Get("Growth")), Pct(d.GetFirst("Growth")));
            sb.AppendFormat("    Vị trí     : {0}\r\n", Pos(lp));
            if (d == me && lp != null)
            {
                var mp = lp.Split(';');
                double yaw;
                if (mp.Length > 3 && double.TryParse(mp[3].Replace("yaw=", ""), NumberStyles.Float, IC, out yaw))
                    sb.AppendFormat("    Đang quay mặt về: {0} ({1:0}°)\r\n", CompassName(YawToCompass(yaw)), ((YawToCompass(yaw) % 360) + 360) % 360);
                if (me.Track.Count > 1)
                {
                    double moved = Dist(me.Track.First(), me.Track.Last());
                    if (moved >= 1)
                        sb.AppendFormat("    Đã di chuyển: {0:0} m về phía {1} (so với lúc bắt đầu)\r\n", moved, CompassName(Bearing(me.Track.First(), me.Track.Last())));
                }
            }
            if (distNow != "")
            {
                double[] a1 = me.Track.Last(), b1 = d.Track.Last();
                double dz = (b1[3] - a1[3]) / 100;
                double brg = Bearing(a1, b1);
                dirNow = CompassName(brg);
                sb.AppendFormat("    Cách bạn   : {0} m về phía {1} (ngang {2} m, {3} {4:0} m)\r\n", distNow, dirNow,
                    (Math.Sqrt(Math.Pow(b1[1] - a1[1], 2) + Math.Pow(b1[2] - a1[2], 2)) / 100).ToString("0", IC),
                    dz >= 0 ? "cao hơn" : "thấp hơn", Math.Abs(dz));
                if (closest != "" && closest != distNow) sb.AppendFormat("    Gần nhất   : {0} m (lúc {1})\r\n", closest, closestAt);
            }
            sb.AppendFormat("    Thấy từ    : {0} đến {1}\r\n", Sec(d.OpenTime), d.CloseTime < 0 ? "hết replay" : Sec(d.CloseTime));
            if (d.Get("attr0.BaseValue") != "")
                sb.AppendFormat("    Chỉ số (chưa rõ tên): attr0={0} attr4={1} attr20={2} attr22={3} attr24={4}\r\n",
                    d.Get("attr0.BaseValue"), d.Get("attr4.BaseValue"), d.Get("attr20.BaseValue"), d.Get("attr22.BaseValue"), d.Get("attr24.BaseValue"));
            var p = (lp ?? "?;?;?").Split(';');
            csv.Add(string.Join(",", new[] { d.Chan.ToString(), Species(d), dead ? "dead" : (mine ? "you" : "alive"), name, Unq(d.Get("SteamId")), gender,
                d.Get("Growth"), p[0], p[1], p.Length > 2 ? p[2] : "", distNow, dirNow, closest, closestAt, d.OpenTime.ToString("0.0", IC), d.CloseTime < 0 ? "" : d.CloseTime.ToString("0.0", IC),
                d.Get("attr0.BaseValue"), d.Get("attr4.BaseValue"), d.Get("attr20.BaseValue"), d.Get("attr22.BaseValue"), d.Get("attr24.BaseValue") }.Select(s => s.Contains(",") ? "\"" + s + "\"" : s)));
        }
        File.WriteAllLines(Path.Combine(OutDir, "dinosaurs.csv"), csv, new UTF8Encoding(true));

        // Danh sách người chơi
        sb.AppendLine();
        sb.AppendFormat("--- NGƯỜI CHƠI TRÊN SERVER ({0}) ---\r\n", psByGuid.Count);
        var pcsv = new List<string> { "player_id,name,join_time_server_s,ping" };
        foreach (var ps in psByGuid.Values.OrderBy(x => Unq(x.Get("PlayerNamePrivate")), StringComparer.OrdinalIgnoreCase))
        {
            string n = Unq(ps.Get("PlayerNamePrivate"));
            var dino = dinos.FirstOrDefault(d => owner(d) == n);
            sb.AppendFormat("  {0,-28} id {1,-5}{2}\r\n", n, ps.Get("PlayerId"), dino != null ? "  (" + Species(dino) + " ở gần)" : "");
            pcsv.Add(string.Join(",", ps.Get("PlayerId"), "\"" + n.Replace("\"", "'") + "\"", ps.Get("StartTime"), ps.Get("CompressedPing")));
        }
        File.WriteAllLines(Path.Combine(OutDir, "players.csv"), pcsv, new UTF8Encoding(true));

        // Thống kê đối tượng khác
        sb.AppendLine();
        sb.AppendLine("--- ĐỐI TƯỢNG KHÁC (số lượng từng xuất hiện) ---");
        var cats = new[] {
            new[] { "Cá (AI)", "/AI/Characters/Fish/" }, new[] { "Cây/nấm ăn được", "/EdiblePlants/" }, new[] { "Trái cây", "/Spawnables/Fruits/" },
            new[] { "Thịt / nội tạng", "/PickablePieces/" }, new[] { "Mùi xác (TICorpseSmell)", "TICorpseSmell" }, new[] { "Tổ / hang", "Burrow" },
            new[] { "Thằn lằn bay doạ (Ptero)", "Ptero_JumpScare" }, new[] { "Tổ trứng", "Nest" } };
        foreach (var c in cats)
            sb.AppendFormat("  {0,-26}: {1}\r\n", c[0], AllActors.Count(a => a.Class != null && a.Class.Contains(c[1])));

        sb.AppendLine();
        sb.AppendLine("--- FILE CHI TIẾT ---");
        sb.AppendLine("  map.html      : bản đồ chấm toạ độ (mở bằng trình duyệt)");
        sb.AppendLine("  dinosaurs.csv : khủng long (loài, người chơi, vị trí, trưởng thành, chỉ số)");
        sb.AppendLine("  players.csv   : danh sách người chơi trên server");
        if (FullOutput)
        {
            sb.AppendLine("  positions.csv : toàn bộ vị trí theo thời gian (khủng long, cá, đồ vật...)");
            sb.AppendLine("  events.csv    : mọi thuộc tính đọc được (dữ liệu thô)");
        }
        else sb.AppendLine("  (chạy với -full hoặc DocDayDu.bat để có thêm positions.csv / events.csv dữ liệu thô)");
        sb.AppendLine();
        sb.AppendLine("Ghi chú: toạ độ cùng đơn vị với minimap trong game. Các chỉ số attrN chưa biết tên");
        sb.AppendLine("(có thể là máu, thể lực, đói, khát...). Chỉ có dữ liệu những gì ở gần bạn lúc quay.");
        File.WriteAllText(Path.Combine(OutDir, "summary.txt"), sb.ToString(), new UTF8Encoding(true));
        WriteMap(me, owner);
        Log.WriteLine(sb.ToString());
    }
}
