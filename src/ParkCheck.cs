using System;
using System.Collections.Generic;

// Cất gara chỉ được khi KHÔNG có người chơi khác trong 50 m (PVN: "Another player is too close. Move at least 50 m away
// from other players to park." – thử 2026-11-07). Từ replay (đang quay) tính người chơi gần bạn nhất -> overlay chỉ số hiện
// "có cất được không". Khoảng cách 3D (như các chỗ khác trong tool); server tính 2D hay 3D chưa kiểm chứng.
class ParkInfo
{
    public double NearestM = -1;          // khoảng cách tới người chơi khác gần nhất (m); -1 = không thấy ai trong tầm replay
    public string Name = "", Species = "";
    public DateTime At = DateTime.Now;
    public const double MinM = 50;
    public bool CanPark { get { return NearestM < 0 || NearestM >= MinM; } }
}

partial class Replay
{
    // Gọi trên luồng đọc replay (sau ParseMore, khi đang quay). null = chưa biết bạn ở đâu.
    public ParkInfo ParkSnapshot()
    {
        Actor me = FindMe();
        if (me == null || me.Track.Count == 0) return null;
        ClassifyNew();
        double[] mp = me.Track[me.Track.Count - 1];
        var pi = new ParkInfo();
        Actor best = null;
        foreach (var a in dinoActors)
        {
            if (a == me || a.CloseTime >= 0 || a.Track.Count == 0) continue;
            if (Unq(a.Get("SteamId")) == "" || a.Get("Controller") != "" || a.Get("bIsDead") == "1") continue; // chỉ người chơi còn sống
            // vị trí đầu tiên có thể là vị trí giả (mới lấy dino ra từ gara) -> đợi 1,6 s xem có nhảy xa không
            if (a.GarageOut == null && CurTime - a.Track[0][0] < 1.6) continue;
            double d = Dist(mp, a.Track[a.Track.Count - 1]);
            if (pi.NearestM < 0 || d < pi.NearestM) { pi.NearestM = d; best = a; }
        }
        if (best != null)
        {
            pi.Species = Species(best) ?? "";
            if (pi.NearestM < ParkInfo.MinM) pi.Name = MakeOwner(PlayerStates())(best); // tên chỉ cần khi quá gần
        }
        return pi;
    }
}
