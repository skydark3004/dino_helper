using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

class Actor
{
    public uint Guid; public uint Chan; public string Class; public double OpenTime, CloseTime = -1;
    public double[] SpawnLoc;
    public Dictionary<uint, string> SubObjects = new Dictionary<uint, string>(); // guid -> class path
    public Dictionary<string, string> First = new Dictionary<string, string>(), Last = new Dictionary<string, string>();
    public string LastPos; public double LastPosTime;
    // tăng mỗi khi có dữ liệu mới -> bản đồ chỉ dựng lại JSON của đối tượng đã thay đổi
    public int Ver; public string JsonCache, JsonKey;
    // phần đường đi đã dựng sẵn dạng JSON (chỉ nối thêm điểm mới)
    public StringBuilder TrackJson; public int TrackDone; public double[] TrackPrev; public string TrackCat;
    public List<double[]> Track = new List<double[]>(); // {time, x, y, z}
    // vị trí gần nhất tại thời điểm t (null nếu chưa biết)
    public double[] PosAt(double t)
    {
        double[] r = null;
        foreach (var s in Track) { if (s[0] <= t) r = s; else break; }
        return r;
    }
    public string Get(string k) { string v; return Last.TryGetValue(k, out v) ? v : ""; }
    public string GetFirst(string k) { string v; return First.TryGetValue(k, out v) ? v : ""; }
}

partial class Replay
{
    public string OutDir = ".";
    public bool DumpGroups;
    public bool PropChecksumBit = true;
    public bool SubDestroyBit = false;
    public bool SubOuterBit = true;

    public int BlocksOk, BlocksFail, PropsOk, PropsFail, NoGroup;
    public Dictionary<uint, Actor> ActorByChan = new Dictionary<uint, Actor>();
    public List<Actor> AllActors = new List<Actor>();
    public Dictionary<string, int> MissingGroups = new Dictionary<string, int>();
    public List<string> Events = new List<string>();
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    partial void OnBunchImpl(Bunch u)
    {
        Channel ch;
        if (!Channels.TryGetValue(u.ChIndex, out ch))
        {
            if (!u.Open && !(u.Partial && !u.PartialInitial)) { if (u.Close) return; }
            ch = new Channel(); Channels[u.ChIndex] = ch;
        }
        try
        {
            if (u.HasExports) ReceiveNetGUIDBunch(u.Ar);
        }
        catch (BitError) { BlocksFail++; return; }

        BitReader ar = u.Ar;
        if (u.Partial)
        {
            if (u.PartialInitial) { ch.PartialAr = u.Ar; ch.PartialOpen = u.Open; ch.PartialMustMap = u.HasMustBeMapped; }
            else if (ch.PartialAr != null) { ch.PartialAr = BitReader.Concat(ch.PartialAr, u.Ar); ch.PartialMustMap |= u.HasMustBeMapped; }
            else return;
            if (!u.PartialFinal) return;
            ar = ch.PartialAr; ch.PartialAr = null;
            u.Open = ch.PartialOpen; u.HasMustBeMapped = ch.PartialMustMap;
        }

        try { ProcessActorBunch(u, ar); }
        catch (BitError e) { BlocksFail++; if (BlocksFail < 8) Log.WriteLine("bunch fail ch{0} t={1:0.00}: {2}", u.ChIndex, CurTime, e.Message); }

        if (u.Close)
        {
            Actor a; if (ActorByChan.TryGetValue(u.ChIndex, out a)) { a.CloseTime = CurTime; ActorByChan.Remove(u.ChIndex); }
            Channels.Remove(u.ChIndex);
        }
    }

    void ReceiveNetGUIDBunch(BitReader b)
    {
        bool hasRepLayoutExport = b.Bit();
        if (hasRepLayoutExport) throw new BitError("replayout export in bunch (unsupported)");
        int n = b.I32();
        for (int i = 0; i < n; i++) LoadObject(b, true);
    }

    void ProcessActorBunch(Bunch u, BitReader ar)
    {
        if (u.HasMustBeMapped)
        {
            int n = ar.U16();
            for (int i = 0; i < n; i++) ar.Packed();
        }
        Actor actor;
        if (!ActorByChan.TryGetValue(u.ChIndex, out actor))
        {
            if (!u.Open) return;
            actor = new Actor { Chan = u.ChIndex, OpenTime = CurTime };
            actor.Guid = LoadObject(ar, false);
            if (actor.Guid != 0 && (actor.Guid & 1) == 0)
            {
                uint arch = LoadObject(ar, false);
                LoadObject(ar, false); // level
                actor.SpawnLoc = OptVector(ar);
                if (ar.Bit()) ReadRotatorShort(ar);
                OptVector(ar); OptVector(ar);
                actor.Class = FullPath(arch) ?? ("guid" + arch);
            }
            else actor.Class = FullPath(actor.Guid) ?? ("static" + actor.Guid);
            ActorByChan[u.ChIndex] = actor; AllActors.Add(actor); ActorByGuid[actor.Guid] = actor;
            if (actor.SpawnLoc != null) Ev(actor, "Spawn", Vec(actor.SpawnLoc));
            else Ev(actor, "Open", "");
        }
        while (!ar.AtEnd)
        {
            bool deleted; bool hasRepLayout; bool isActor; string cls;
            uint objGuid = ReadContentBlockHeader(ar, actor, out deleted, out hasRepLayout, out isActor, out cls);
            if (deleted) { BlocksOk++; continue; }
            uint nbits = ar.Packed();
            BitReader p = ar.Sub(nbits);
            BlocksOk++;
            ExportGroup g = FindGroup(cls);
            if (g == null)
            {
                NoGroup++;
                int c; MissingGroups.TryGetValue(cls ?? "?", out c); MissingGroups[cls ?? "?"] = c + 1;
                continue;
            }
            string target = isActor ? "" : ShortName(cls) + ".";
            try
            {
                if (hasRepLayout) ReceiveProperties(p, g, actor, target);
                PropsOk++;
            }
            catch (BitError e) { PropsFail++; if (PropsFail < 8) Log.WriteLine("props fail {0}: {1}", g.Path, e.Message); }
        }
    }

    uint ReadContentBlockHeader(BitReader ar, Actor actor, out bool deleted, out bool hasRepLayout, out bool isActor, out string cls)
    {
        deleted = false; cls = null;
        hasRepLayout = ar.Bit();
        isActor = ar.Bit();
        if (isActor) { cls = actor.Class; return actor.Guid; }
        uint guid = LoadObject(ar, false);
        if (SubDestroyBit && ar.Bit()) { ar.U8(); deleted = true; return guid; }
        bool stably = ar.Bit();
        if (stably)
        {
            string c2; if (!actor.SubObjects.TryGetValue(guid, out c2)) c2 = FullPath(guid);
            cls = c2; return guid;
        }
        uint clsGuid = LoadObject(ar, false);
        if (SubOuterBit && !ar.Bit()) LoadObject(ar, false);
        if (clsGuid == 0) { deleted = true; return guid; }
        cls = FullPath(clsGuid);
        actor.SubObjects[guid] = cls;
        return guid;
    }

    // tên subobject cố định -> class
    static readonly Dictionary<string, string> SubAlias = new Dictionary<string, string> {
        { "AttributeSetBase", "TIAttributeSetDinosaur" },
        { "CharMoveComp", "TIDinosaurMovementComponent" },
        { "AbilitySystemComponent", "TIAbilitySystemComponent" },
    };
    Dictionary<string, ExportGroup> groupCache = new Dictionary<string, ExportGroup>();
    ExportGroup FindGroup(string path)
    {
        if (path == null) return null;
        ExportGroup g;
        if (groupCache.TryGetValue(path, out g)) return g;
        if (!GroupByPath.TryGetValue(path, out g))
        {
            // "/Game/X/BP_A.Default__BP_A_C" -> "/Game/X/BP_A.BP_A_C"
            string p = path.Replace("Default__", "");
            if (!GroupByPath.TryGetValue(p, out g))
            {
                // subobject theo tên: lấy phần sau dấu chấm cuối, so với tên class
                string last = p.Substring(p.LastIndexOfAny(new[] { '.', ':' }) + 1);
                // actor đặt sẵn trên map: "Ultra_Dynamic_Sky_C_UAID_xxx" / "BP_X_C_12" -> "Ultra_Dynamic_Sky_C"
                int ua = last.IndexOf("_UAID_");
                if (ua > 0) last = last.Substring(0, ua);
                last = System.Text.RegularExpressions.Regex.Replace(last, @"(_C)_\d+$", "$1");
                string alias;
                if (SubAlias.TryGetValue(last, out alias)) last = alias;
                foreach (var kv in GroupByPath)
                {
                    string k = kv.Key; string kl = k.Substring(k.LastIndexOfAny(new[] { '.', '/' }) + 1);
                    if (kl == last || kl == last + "_C") { g = kv.Value; break; }
                }
            }
        }
        if (g != null && (g.Path.EndsWith("_ClassNetCache"))) g = null;
        groupCache[path] = g;
        return g;
    }

    void ReceiveProperties(BitReader p, ExportGroup g, Actor actor, string target)
    {
        if (PropChecksumBit) p.Bit();
        while (true)
        {
            uint handle = p.Packed();
            if (handle == 0) break;
            handle--;
            uint nb = p.Packed();
            if (nb == 0) continue;
            BitReader v = p.Sub(nb);
            string name; if (!g.Fields.TryGetValue(handle, out name)) name = "h" + handle;
            OnProperty(actor, target, g, handle, name, v);
        }
        // phần còn lại là RPC / custom delta
        if (!p.AtEnd) ReadFields(p, g, actor, target);
    }

    public Dictionary<string, int> FieldStats = new Dictionary<string, int>();
    void ReadFields(BitReader p, ExportGroup g, Actor actor, string target)
    {
        ExportGroup cache;
        if (!GroupByPath.TryGetValue(g.Path + "_ClassNetCache", out cache)) { Stat("nocache " + ShortName(g.Path)); return; }
        while (!p.AtEnd)
        {
            uint h = p.SerInt(Math.Max(cache.NumExports, 2));
            uint nb = p.Packed();
            BitReader v = p.Sub(nb);
            string name; if (!cache.Fields.TryGetValue(h, out name)) name = "h" + h;
            Stat(ShortName(g.Path) + "." + name);
            OnField(actor, target, name, v);
        }
    }
    void Stat(string k) { int c; FieldStats.TryGetValue(k, out c); FieldStats[k] = c + 1; }
    void OnField(Actor a, string target, string name, BitReader v) { }

    string ShortName(string path)
    {
        if (path == null) return "?";
        string s = path.Substring(path.LastIndexOfAny(new[] { '.', '/' }) + 1);
        if (s.StartsWith("Default__")) s = s.Substring(9);
        return s;
    }

    // ---------- giải mã giá trị ----------
    void OnProperty(Actor a, string target, ExportGroup g, uint handle, string name, BitReader v)
    {
        string val;
        curActor = a;
        try { val = DecodeValue(g, handle, name, v); }
        catch (BitError) { val = "?raw" + v.End; }
        if (name == "ReplicatedMovement" && val == null) { val = "?mov" + v.End + ":" + Hex(v); }
        if (val == null) return;
        string key = target + name;
        if (g.Path.EndsWith("TIAttributeSetDinosaur")) key = "attr" + handle + "." + name;
        Ev(a, key, val);
    }

    string DecodeValue(ExportGroup g, uint handle, string name, BitReader v)
    {
        switch (name)
        {
            case "ReplicatedMovement": return RepMovement(v, curActor);
            case "PlayerNamePrivate": case "SteamId": case "SkinCode": return Quote(v.FString());
            case "PlayerState": case "Owner": case "Instigator": case "Controller": case "Character": case "MovementBase":
                { uint gd = v.Packed(); a_lastGuid = gd; return "guid:" + gd; }
            case "#215": case "#216": return null; // Role / RemoteRole
            case "RepAnimMontageInfo":
                {
                    string mt = MontageName(v);
                    if (mt == null) { v.Pos = 0; break; }
                    // nhặt sỏi -> nhớ chỗ này là bãi sỏi
                    if (mt.Contains("Gastrolith") && curActor != null && GastroStore.Add(curActor.LastPos, Species(curActor)))
                        NewGastro++;
                    return mt;
                }
        }
        long n = v.End;
        if (n == 1) return v.Bit() ? "1" : "0";
        if (n == 32)
        {
            uint raw = v.U32();
            float f = BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
            if (!float.IsNaN(f) && Math.Abs(f) > 1e-6 && Math.Abs(f) < 1e9) return f.ToString("0.####", IC);
            return raw.ToString();
        }
        if (n == 64) return BitConverter.ToDouble(BitConverter.GetBytes(v.Bits(64)), 0).ToString("0.###", IC);
        if (n == 8) return v.U8().ToString();
        if (n == 16) return v.U16().ToString();
        // thử chuỗi
        if (n >= 32)
        {
            long save = v.Pos;
            v.Soft = true; v.Failed = false;
            string s = v.FString();
            bool ok = !v.Failed && v.AtEnd && s.Length > 0 && s.All(c => c >= 32 && c < 0xFFFE);
            v.Soft = false; v.Failed = false;
            if (ok) return Quote(s);
            v.Pos = save;
        }
        return "bits" + n;
    }
    uint a_lastGuid;
    public int NewGastro; // số chỗ sỏi mới ghi nhận trong lần đọc này

    // RepAnimMontageInfo (GAS): tìm tham chiếu tới asset animation (packed NetGUID) trong struct -> "montage:<tên>@<bit>"
    string MontageName(BitReader v)
    {
        for (long off = 0; off + 8 <= v.End; off++)
        {
            v.Pos = off; v.Soft = true; v.Failed = false;
            uint g = v.Packed();
            bool bad = v.Failed; v.Soft = false; v.Failed = false;
            if (bad || g == 0) continue;
            string p = FullPath(g);
            if (p != null && p.Contains("/Animations/"))
            {
                int k = p.LastIndexOf('/'); string nm = k >= 0 ? p.Substring(k + 1) : p;
                k = nm.IndexOf('.'); if (k > 0) nm = nm.Substring(0, k);
                return "montage:" + nm + "@" + off;
            }
        }
        return null;
    }

    static string Hex(BitReader v)
    {
        var sb = new StringBuilder();
        for (long i = 0; i < v.End; i++) sb.Append(((v.D[i >> 3] >> (int)(i & 7)) & 1) != 0 ? '1' : '0');
        return sb.ToString();
    }
    static string Quote(string s) { return "\"" + s.Replace("\"", "'") + "\""; }

    Actor curActor;
    // hệ số lượng tử hoá vị trí theo class (dò bằng cách so với toạ độ lúc spawn)
    Dictionary<string, double> LocScale = new Dictionary<string, double>();

    string RepMovement(BitReader v, Actor a)
    {
        double[] loc, rot, vel;
        if (!RepMovementRaw(v, out loc, out rot, out vel)) return null;
        double sc;
        if (!LocScale.TryGetValue(a.Class, out sc))
        {
            if (a.SpawnLoc == null) sc = 1;
            else
            {
                sc = 1; double best = double.MaxValue;
                foreach (double s in new[] { 1.0, 10.0, 100.0 })
                {
                    double d = Math.Abs(loc[0] / s - a.SpawnLoc[0]) + Math.Abs(loc[1] / s - a.SpawnLoc[1]);
                    if (d < best) { best = d; sc = s; }
                }
                LocScale[a.Class] = sc;
            }
        }
        for (int i = 0; i < 3; i++) loc[i] /= sc;
        return MovStr(loc, rot[1], vel);
    }

    // FRepMovement::NetSerialize (UE5)
    bool RepMovementRaw(BitReader v, out double[] oloc, out double[] orot, out double[] ovel)
    {
        oloc = orot = ovel = null;
        // 6 cách đọc (số bit cờ 4/3/2 × góc quay 1 hoặc 2 byte): giải mã mỗi cách MỘT lần,
        // rồi chọn cách để thừa ít bit nhất (0..8), cùng thứ tự ưu tiên như trước
        var left = new long[6]; var locs = new double[6][]; var rots = new double[6][]; var vels = new double[6][];
        int k = 0;
        foreach (int flagBits in new[] { 4, 3, 2 })
            foreach (bool shortRot in new[] { false, true })
            {
                v.Pos = 0; v.Soft = true; v.Failed = false;
                uint flags = (uint)v.Bits(flagBits);
                bool physics = (flags & 2) != 0, serverFrame = flagBits >= 3 && (flags & 4) != 0, accel = flagBits >= 4 && (flags & 8) != 0;
                locs[k] = QuantVec(v, 1);
                rots[k] = shortRot ? ReadRotatorShort(v) : ReadRotatorByte(v);
                vels[k] = QuantVec(v, 1);
                if (accel) QuantVec(v, 1);
                if (physics) QuantVec(v, 1);
                if (serverFrame) { v.Packed(); v.Packed(); }
                left[k] = v.Failed ? -1 : v.Left;
                k++;
            }
        v.Soft = false;
        for (int slack = 0; slack <= 8; slack++)
            for (int i = 0; i < 6; i++)
                if (left[i] == slack) { oloc = locs[i]; orot = rots[i]; ovel = vels[i]; return true; }
        return false;
    }

    static string MovStr(double[] loc, double yaw, double[] vel)
    {
        return string.Format(IC, "{0:0};{1:0};{2:0};yaw={3:0};speed={4:0}", loc[0], loc[1], loc[2], yaw, Math.Sqrt(vel[0] * vel[0] + vel[1] * vel[1] + vel[2] * vel[2]));
    }

    double[] QuantVec(BitReader v, double scale)
    {
        LastScale = scale;
        uint info = v.SerInt(1u << 7);
        int bits = (int)(info & 63); bool extra = (info >> 6) != 0;
        if (bits > 0)
        {
            double[] r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                ulong x = v.Bits(bits); ulong sign = 1UL << (bits - 1);
                r[i] = (double)((long)(x ^ sign) - (long)sign);
            }
            // ExtraInfo = có scale factor; không biết chính xác scale -> giả định 10 (RoundOneDecimal) cho vị trí sẽ chỉnh sau
            if (extra) for (int i = 0; i < 3; i++) r[i] /= LastScale;
            return r;
        }
        if (extra) return new[] { v.F64(), v.F64(), v.F64() };
        return new double[] { v.F32(), v.F32(), v.F32() };
    }
    public double LastScale = 100;

    double[] ReadRotatorByte(BitReader v)
    {
        double[] r = new double[3];
        for (int i = 0; i < 3; i++) if (v.Bit()) r[i] = v.U8() * 360.0 / 256;
        return r;
    }
    double[] ReadRotatorShort(BitReader v)
    {
        double[] r = new double[3];
        for (int i = 0; i < 3; i++) if (v.Bit()) r[i] = v.U16() * 360.0 / 65536;
        return r;
    }
    double[] OptVector(BitReader v)
    {
        if (!v.Bit()) return null;
        bool quant = v.Bit();
        if (quant) return QuantVec(v, 10);
        return new[] { v.F64(), v.F64(), v.F64() };
    }
    static string Vec(double[] p) { return string.Format(IC, "{0:0};{1:0};{2:0}", p[0], p[1], p[2]); }

    // External data của nhân vật = FCharacterReplaySample: Location, Velocity, Acceleration (packed <10,24>), Rotation (byte), RemoteViewPitch, Time
    public Dictionary<uint, Actor> ActorByGuid = new Dictionary<uint, Actor>();
    public int ExtOk, ExtFail;
    public void OnExternalData(uint guid, byte[] data, uint nbits)
    {
        Actor a;
        if (!ActorByGuid.TryGetValue(guid, out a) || Species(a) == null) return; // chỉ nhân vật mới có FCharacterReplaySample
        var v = new BitReader(data, nbits);
        try
        {
            double[] loc = QuantVec(v, 10);
            double[] vel = QuantVec(v, 10);
            QuantVec(v, 10);
            double[] rot = ReadRotatorByte(v);
            Ev(a, "Move", MovStr(loc, rot[1], vel));
            ExtOk++;
        }
        catch (BitError) { ExtFail++; }
    }

    void Ev(Actor a, string prop, string val)
    {
        a.Ver++;
        a.Last[prop] = val;
        if (!a.First.ContainsKey(prop)) a.First[prop] = val;
        if (prop == "ReplicatedMovement" || prop == "Move" || prop == "Spawn")
        {
            string[] p = val.Split(';');
            double x, y, z;
            if (!double.TryParse(p[0], NumberStyles.Float, IC, out x) || !double.TryParse(p[1], NumberStyles.Float, IC, out y) || !double.TryParse(p[2], NumberStyles.Float, IC, out z)) return;
            // bản đồ chỉ khoảng ±10 km -> toạ độ quá xa là giải mã sai, bỏ qua
            const double MaxCoord = 2000000;
            if (Math.Abs(x) > MaxCoord || Math.Abs(y) > MaxCoord || Math.Abs(z) > MaxCoord) { BadPositions++; return; }
            // yaw (hướng quay mặt) nếu có: "yaw=93" -> phần tử thứ 5
            double yaw;
            if (p.Length > 3 && double.TryParse(p[3].Replace("yaw=", ""), NumberStyles.Float, IC, out yaw)) a.Track.Add(new[] { CurTime, x, y, z, yaw });
            else a.Track.Add(new[] { CurTime, x, y, z });
            a.LastPos = val; a.LastPosTime = CurTime;
            // dòng cho positions.csv / events.csv chỉ cần khi xuất dữ liệu thô (tạo hàng trăm nghìn chuỗi rất tốn thời gian)
            if (FullOutput) Positions.Add(string.Format(IC, "{0:0.000},{1},{2},{3},{4},{5},{6},{7}", CurTime, a.Chan, ShortName(a.Class), p[0], p[1], p[2],
                p.Length > 3 ? p[3].Replace("yaw=", "") : "", p.Length > 4 ? p[4].Replace("speed=", "") : ""));
        }
        if (FullOutput) Events.Add(string.Format(IC, "{0:0.000},{1},{2},{3},{4}", CurTime, a.Chan, ShortName(a.Class), prop, val));
    }

    public void Finish()
    {
        if (DumpGroups)
            foreach (var g in GroupByPath.Values)
                Log.WriteLine("{0} [{1}]: {2}", g.Path, g.NumExports, string.Join(", ", g.Fields.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value).ToArray()));
        Log.WriteLine("blocks ok {0} fail {1} | props ok {2} fail {3} | no group {4} | ext ok {5} fail {6}", BlocksOk, BlocksFail, PropsOk, PropsFail, NoGroup, ExtOk, ExtFail);
        foreach (var kv in MissingGroups.OrderByDescending(k => k.Value).Take(8)) Log.WriteLine("  missing group {0}: {1}", kv.Key, kv.Value);
        foreach (var kv in FieldStats.OrderByDescending(k => k.Value).Take(30)) Log.WriteLine("  field {0}: {1}", kv.Key, kv.Value);
        Directory.CreateDirectory(OutDir);
        // dữ liệu thô rất nặng (vài MB/phút) -> chỉ ghi khi chạy với -full
        if (FullOutput)
        {
            File.WriteAllLines(Path.Combine(OutDir, "events.csv"), new[] { "time,channel,class,property,value" }.Concat(Events.Select(CsvSafe)), new UTF8Encoding(true));
            File.WriteAllLines(Path.Combine(OutDir, "positions.csv"), new[] { "time,channel,class,x,y,z,yaw,speed" }.Concat(Positions), new UTF8Encoding(true));
        }
        WriteReport();
    }
    public List<string> Positions = new List<string>();
    public int BadPositions;
    public bool FullOutput;
    static string CsvSafe(string line)
    {
        // giá trị (cột cuối) có thể chứa dấu phẩy -> bọc ngoặc kép
        int i = 0; for (int k = 0; k < 4; k++) i = line.IndexOf(',', i) + 1;
        string val = line.Substring(i);
        if (val.Contains(",") && !val.StartsWith("\"")) val = "\"" + val.Replace("\"", "'") + "\"";
        return line.Substring(0, i) + val;
    }
}
