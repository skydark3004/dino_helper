using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Cắt 1 đoạn replay thành file mới (file gốc không bị đụng tới).
// File mới = header file + chunk Header + checkpoint gần nhất <= giờ bắt đầu + các chunk dữ liệu/checkpoint/sự kiện sau đó.
static class ReplayCut
{
    class Chunk { public uint Type; public int Off, Start, Size; public uint T1, T2; public int TimeOff; public string Id, Group; public int DataStart; }

    static List<Chunk> ReadChunks(byte[] d, out int headerEnd, out int lenOff, out int liveOff, out int tsOff)
    {
        var r = new ByteReader(d, 0, d.Length);
        if (r.U32() != 0x1CA2E27F) throw new Exception("không phải file replay");
        r.U32(); int ncv = r.I32(); r.Pos += 20 * ncv;
        lenOff = r.Pos; r.U32(); r.U32(); r.U32();
        r.FString();
        liveOff = r.Pos; r.U32();
        tsOff = r.Pos; r.Pos += 8;
        uint comp = r.U32(), enc = r.U32(); int kl = r.I32(); r.Pos += kl;
        if (comp != 0 || enc != 0) throw new Exception("replay bị nén/mã hoá - chưa hỗ trợ");
        headerEnd = r.Pos;
        var list = new List<Chunk>();
        int pos = r.Pos;
        while (pos + 8 <= d.Length)
        {
            var c = new Chunk { Type = BitConverter.ToUInt32(d, pos), Size = BitConverter.ToInt32(d, pos + 4), Off = pos, Start = pos + 8 };
            if (c.Size < 0 || (long)c.Start + c.Size > d.Length) break; // chunk cuối chưa ghi xong
            if (c.Type == 1)
            {
                c.TimeOff = c.Start; c.T1 = BitConverter.ToUInt32(d, c.Start); c.T2 = BitConverter.ToUInt32(d, c.Start + 4);
                c.DataStart = c.Start + 16;
            }
            else if (c.Type == 2 || c.Type == 3)
            {
                var cr = new ByteReader(d, c.Start, c.Start + c.Size);
                c.Id = cr.FString(); c.Group = cr.FString(); cr.FString();
                c.TimeOff = cr.Pos; c.T1 = cr.U32(); c.T2 = cr.U32(); cr.I32();
                c.DataStart = cr.Pos;
            }
            list.Add(c);
            pos = c.Start + c.Size;
        }
        return list;
    }

    public static void ListChunks(string file, TextWriter o)
    {
        byte[] d = ReadShared(file);
        int he, lo, lv, ts;
        var cs = ReadChunks(d, out he, out lo, out lv, out ts);
        o.WriteLine("length {0}ms live {1}", BitConverter.ToUInt32(d, lo), BitConverter.ToUInt32(d, lv));
        foreach (var c in cs)
        {
            string extra = "";
            if (c.Type == 2 || c.Type == 3)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 24 && c.DataStart + i < c.Start + c.Size; i++) sb.Append(d[c.DataStart + i].ToString("x2")).Append(' ');
                extra = string.Format(" id={0} group={1} data={2}", c.Id, c.Group, sb);
            }
            if (c.Type == 1)
            {
                // thời gian frame đầu / cuối
                extra = string.Format(" mem={0} firstFrame={1}", BitConverter.ToUInt32(d, c.Start + 12), BitConverter.ToSingle(d, c.DataStart + 4));
            }
            o.WriteLine("{0,10} type {1} size {2,8} t {3,8}..{4,8}{5}", c.Off, c.Type, c.Size, c.T1, c.T2, extra);
        }
    }

    // Đi qua 1 frame (giống Replay.FrameComplete). Trả về vị trí sau frame.
    static int SkipFrame(byte[] d, int pos, int end)
    {
        var r = new ByteReader(d, pos, end);
        r.I32(); r.F32();
        uint n = r.Packed();
        for (uint i = 0; i < n; i++)
        {
            r.Packed();
            if (r.Packed() != 0) { r.FString(); r.Packed(); }
            if (r.U8() != 0) { r.Packed(); r.U32(); if (r.U8() != 0) r.Packed(); else { r.FString(); r.I32(); } }
        }
        n = r.Packed(); for (uint i = 0; i < n; i++) r.Skip(r.I32());
        n = r.Packed(); for (uint i = 0; i < n; i++) r.FString();
        r.U64();
        while (true) { uint nb = r.Packed(); if (nb == 0) break; r.Packed(); r.Skip((nb + 7) >> 3); }
        while (true) { r.Packed(); int sz = r.I32(); if (sz == 0) break; r.Skip(sz); }
        return r.Pos;
    }

    static void AddTime(byte[] d, int off, int deltaMs)
    {
        uint v = BitConverter.ToUInt32(d, off);
        Array.Copy(BitConverter.GetBytes((uint)Math.Max(0, (long)v + deltaMs)), 0, d, off, 4);
    }
    static void AddFrameTime(byte[] d, int off, int deltaMs)
    {
        float v = BitConverter.ToSingle(d, off);
        Array.Copy(BitConverter.GetBytes((float)Math.Max(0, v + deltaMs / 1000.0)), 0, d, off, 4);
    }

    // Cắt từ fromMs đến toMs (toMs <= 0: tới hết). rebase = true: đoạn cắt bắt đầu từ 0:00.
    // Trả về mô tả ngắn.
    // mergeMs > 0: gộp chunk dữ liệu thành chunk ≤ mergeMs (mặc định 20 s); 0 = giữ nguyên chunk.
    public static string Cut(string file, long fromMs, long toMs, bool rebase, string outFile, TextWriter log, uint mergeMs = 20000)
    {
        byte[] d = ReadShared(file);
        int he, lenOff, liveOff, tsOff;
        var cs = ReadChunks(d, out he, out lenOff, out liveOff, out tsOff);
        uint endAll = 0; foreach (var c in cs) if (c.Type == 1) endAll = Math.Max(endAll, c.T2);
        if (toMs > 0 && toMs <= fromMs) throw new Exception("thời gian kết thúc phải sau thời gian bắt đầu");
        if (fromMs >= endAll) throw new Exception("thời gian bắt đầu (" + FormatTime(fromMs) + ") vượt quá độ dài replay (" + FormatTime(endAll) + ")");
        Chunk cp = null;
        foreach (var c in cs) if (c.Type == 2 && c.T1 <= fromMs) cp = c;
        if (cp == null && fromMs > 0) throw new Exception("không có checkpoint nào trước thời điểm bắt đầu");
        uint t0 = cp == null ? 0 : cp.T1;
        int delta = rebase ? -(int)t0 : 0;
        uint lastT = t0;

        // checkpoint đầu đoạn -> frame dữ liệu đầu tiên (game chỉ đọc checkpoint khi tua, mở file là phát từ khối dữ liệu đầu)
        byte[] first = null;
        if (cp != null)
        {
            // thời điểm frame phải > 0: game bỏ qua frame ở đúng 0,000 s (file gốc: frame đầu ở 0,021 s)
            first = CheckpointToFrame(d, cp.DataStart, cp.Start + cp.Size, (float)((t0 + delta) / 1000.0 + 0.001), log);
            if (first == null) throw new Exception("không chuyển được checkpoint");
        }

        var o = new MemoryStream();
        o.Write(d, 0, he);
        foreach (var c in cs)
        {
            bool keep;
            if (c.Type == 0) keep = true;
            else if (c.Type == 1) keep = c.T1 >= t0 && (toMs <= 0 || c.T1 < toMs);
            else keep = (cp == null ? c.T1 >= t0 : c.T1 > t0) && (toMs <= 0 || c.T1 < toMs);   // checkpoint đầu đoạn đã thành frame
            if (!keep) continue;
            int at = (int)o.Position, shift;
            if (c.Type == 1 && first != null)
            {
                // khối dữ liệu đầu tiên: chèn frame từ checkpoint vào ĐẦU khối (game bỏ qua khối riêng có giờ đầu = giờ cuối)
                uint len = BitConverter.ToUInt32(d, c.Start + 8), mem = BitConverter.ToUInt32(d, c.Start + 12);
                WI32(o, 1); WI32(o, c.Size + first.Length);
                o.Write(d, c.Start, 8);                                         // giờ đầu/cuối (sửa bên dưới)
                o.Write(BitConverter.GetBytes(len + (uint)first.Length), 0, 4);
                o.Write(BitConverter.GetBytes(mem + (uint)first.Length), 0, 4);
                o.Write(first, 0, first.Length);
                o.Write(d, c.DataStart, c.Start + c.Size - c.DataStart);
                shift = at - c.Off + first.Length;                              // vị trí frame gốc trong bản sao
                // giờ của khối nằm trước phần chèn: sửa riêng
                if (rebase)
                {
                    byte[] bb = o.GetBuffer();
                    AddTime(bb, at + 8, delta); AddTime(bb, at + 12, delta);
                }
                first = null; lastT = Math.Max(lastT, c.T2);
                if (!rebase) continue;
                byte[] b2 = o.GetBuffer();
                for (int p = c.DataStart, end = c.Start + c.Size; p < end; p = SkipFrame(d, p, end)) AddFrameTime(b2, p + 4 + shift, delta);
                continue;
            }
            o.Write(d, c.Off, 8 + c.Size);
            if (c.Type == 1 || c.Type == 2) lastT = Math.Max(lastT, c.T2);
            if (!rebase || c.Type == 0) continue;
            // sửa thời gian ngay trong bản sao
            byte[] buf = o.GetBuffer();
            shift = at - c.Off;
            AddTime(buf, c.TimeOff + shift, delta); AddTime(buf, c.TimeOff + 4 + shift, delta);
            if (c.Type == 1)
            {
                int p = c.DataStart, end = c.Start + c.Size;
                while (p < end) { AddFrameTime(buf, p + 4 + shift, delta); p = SkipFrame(d, p, end); }
            }
            else if (c.Type == 2)
            {
                // dữ liệu checkpoint: int64 PacketOffset (tính từ sau chính nó) -> frame của checkpoint
                long po = BitConverter.ToInt64(d, c.DataStart);
                int f = c.DataStart + 8 + (int)po;
                if (po > 0 && f + 8 <= c.Start + c.Size) AddFrameTime(buf, f + 4 + shift, delta);
            }
        }
        byte[] res = o.ToArray();
        uint newLen = (uint)(lastT - t0 + (rebase ? 0 : t0));
        Array.Copy(BitConverter.GetBytes(newLen), 0, res, lenOff, 4);
        Array.Copy(BitConverter.GetBytes(0u), 0, res, liveOff, 4);   // không còn "đang quay"
        if (rebase)
        {
            long ts = BitConverter.ToInt64(d, tsOff) + t0 * TimeSpan.TicksPerMillisecond;
            Array.Copy(BitConverter.GetBytes(ts), 0, res, tsOff, 8);
        }
        // gộp chunk nhỏ (quay bằng thông số nhanh 0,125 s) thành chunk ~15 s -> xem mượt cả trên máy chưa chỉnh config
        string mergeInfo = "";
        if (mergeMs > 0)
        {
            int nIn, nOut;
            res = MergeData(res, mergeMs, out nIn, out nOut);
            mergeInfo = string.Format(", gộp {0} → {1} chunk", nIn, nOut);
        }
        File.WriteAllBytes(outFile, res);
        // giờ sửa file = giờ thật kết thúc đoạn (danh sách của tool tính giờ quay = giờ sửa - độ dài)
        try
        {
            DateTime origEnd = File.GetLastWriteTime(file);
            uint origLen = BitConverter.ToUInt32(d, lenOff);
            File.SetLastWriteTime(outFile, origEnd.AddMilliseconds(-(double)origLen + lastT));
        }
        catch { }
        return string.Format("bắt đầu thật từ {0} (điểm lưu gần nhất, game lưu mỗi 15 giây) → {1}, dài {2}, {3:0.0} MB{4}",
            FormatTime(t0), FormatTime(t0 + newLen - (rebase ? 0 : t0)), FormatTime(newLen - (rebase ? 0 : t0)), res.Length / 1048576.0, mergeInfo);
    }

    static void WPacked(Stream s, uint v)
    {
        do { byte b = (byte)((v & 0x7F) << 1); v >>= 7; if (v != 0) b |= 1; s.WriteByte(b); } while (v != 0);
    }
    static void WBytes(Stream s, byte[] d, int from, int to) { s.Write(d, from, to - from); }
    static void WI32(Stream s, int v) { s.Write(BitConverter.GetBytes(v), 0, 4); }

    // Chuyển checkpoint thành 1 frame dữ liệu bình thường (để game phát được ngay từ đầu file cắt, không cần tua):
    // bảng NetGUID của checkpoint -> NetExportGuids, bảng NetFieldExportGroup -> NetFieldExports, rồi frame của checkpoint.
    // Trả về null nếu cấu trúc không như mong đợi.
    static byte[] CheckpointToFrame(byte[] d, int ds, int end, float frameTime, TextWriter log)
    {
        var r = new ByteReader(d, ds, end);
        long po = (long)r.U64();
        int frameAt = ds + 8 + (int)po;
        r.I32();                                        // level của checkpoint
        int nDel = r.I32(); for (int i = 0; i < nDel; i++) r.FString();   // startup actor đã bị xoá (bỏ qua)
        // bảng NetGUID
        int nGuid = r.I32();
        var names = new List<int[]>();                  // bảng tên: [từ, đến] trong d
        var guids = new List<uint>(); var outers = new List<uint>(); var paths = new List<int[]>(); var gflags = new List<byte>();
        for (int i = 0; i < nGuid; i++)
        {
            uint g = r.Packed(), outer = r.Packed();
            int[] p;
            if (r.U8() != 0) { int a = r.Pos; r.FString(); p = new[] { a, r.Pos }; names.Add(p); }
            else p = names[(int)r.Packed()];
            guids.Add(g); outers.Add(outer); paths.Add(p); gflags.Add(r.U8());
        }
        // bảng NetFieldExportGroup
        var nfe = new MemoryStream(); int nfeCount = 0;
        uint nGroups = r.U32();
        for (uint gi = 0; gi < nGroups; gi++)
        {
            int pa = r.Pos; r.FString(); int pb = r.Pos;
            uint idx = r.Packed(), num = r.Packed();
            bool first = true;
            for (uint f = 0; f < num; f++)
            {
                int fa = r.Pos;
                if (r.U8() == 0) continue;
                r.Packed(); r.U32();
                if (r.U8() != 0) r.Packed(); else { r.FString(); r.I32(); }
                WPacked(nfe, idx);
                if (first) { WPacked(nfe, 1); WBytes(nfe, d, pa, pb); WPacked(nfe, num); first = false; } else WPacked(nfe, 0);
                WBytes(nfe, d, fa, r.Pos); nfeCount++;
            }
            if (first) { WPacked(nfe, idx); WPacked(nfe, 1); WBytes(nfe, d, pa, pb); WPacked(nfe, num); nfe.WriteByte(0); nfeCount++; }
        }
        if (r.Pos != frameAt) { log.WriteLine("checkpoint: cấu trúc lệch ({0} != {1})", r.Pos - ds, frameAt - ds); return null; }

        // NetExportGuids: outer trước, bỏ mục không có đường dẫn (actor động - được tạo qua bunch mở kênh)
        var neg = new MemoryStream(); int negCount = 0;
        var pos = new Dictionary<uint, int>(); for (int i = 0; i < guids.Count; i++) pos[guids[i]] = i;
        var done = new HashSet<uint>();
        Action<int> emit = null;
        emit = i =>
        {
            if (!done.Add(guids[i])) return;
            int o; if (outers[i] != 0 && pos.TryGetValue(outers[i], out o)) emit(o);
            if (new ByteReader(d, paths[i][0], paths[i][1]).FString().Length == 0) return; // đường dẫn rỗng
            var e = new MemoryStream();
            WPacked(e, guids[i]);
            e.WriteByte((byte)(1 | ((gflags[i] & 1) != 0 ? 2 : 0)));     // có đường dẫn (+ NoLoad)
            WPacked(e, outers[i]); if (outers[i] != 0) e.WriteByte(0);  // outer: chỉ tham chiếu
            WBytes(e, d, paths[i][0], paths[i][1]);
            WI32(neg, (int)e.Length); e.WriteTo(neg); negCount++;
        };
        for (int i = 0; i < guids.Count; i++) emit(i);

        // frame của checkpoint: chèn 2 bảng trên vào trước phần của chính nó
        var fr = new ByteReader(d, frameAt, end);
        int level = fr.I32(); fr.F32();
        int na = fr.Pos; uint n0 = fr.Packed(); int nb = fr.Pos;
        for (uint i = 0; i < n0; i++)
        {
            fr.Packed();
            if (fr.Packed() != 0) { fr.FString(); fr.Packed(); }
            if (fr.U8() != 0) { fr.Packed(); fr.U32(); if (fr.U8() != 0) fr.Packed(); else { fr.FString(); fr.I32(); } }
        }
        int nEnd = fr.Pos;
        uint g0 = fr.Packed(); int gb = fr.Pos;
        for (uint i = 0; i < g0; i++) fr.Skip(fr.I32());
        int gEnd = fr.Pos;
        int frameEnd = SkipFrame(d, frameAt, end);
        if (frameEnd != end) log.WriteLine("checkpoint: còn {0} byte sau frame", end - frameEnd);

        var o2 = new MemoryStream();
        WI32(o2, level); o2.Write(BitConverter.GetBytes(frameTime), 0, 4);
        WPacked(o2, (uint)(nfeCount + n0)); nfe.WriteTo(o2); WBytes(o2, d, nb, nEnd);
        WPacked(o2, (uint)(negCount + g0)); neg.WriteTo(o2); WBytes(o2, d, gb, gEnd);
        WBytes(o2, d, gEnd, frameEnd);
        log.WriteLine("checkpoint -> frame: {0} NetGUID (bảng có {1}), {2} field export, {3} group, frame gốc nfe {4} guid {5}",
            negCount, nGuid, nfeCount, nGroups, n0, g0);
        return o2.ToArray();
    }

    // In tóm tắt n frame đầu của file (để so khối mở đầu)
    public static void DumpFrames(string file, int maxFrames, TextWriter o)
    {
        byte[] d = ReadShared(file);
        int he, lo, lv, ts;
        var cs = ReadChunks(d, out he, out lo, out lv, out ts);
        int shown = 0;
        foreach (var c in cs)
        {
            if (c.Type != 1) continue;
            int p = c.DataStart, end = c.Start + c.Size;
            while (p < end && shown < maxFrames)
            {
                var r = new ByteReader(d, p, end);
                int level = r.I32(); float t = r.F32();
                uint nfe = r.Packed();
                for (uint i = 0; i < nfe; i++)
                {
                    r.Packed(); if (r.Packed() != 0) { r.FString(); r.Packed(); }
                    if (r.U8() != 0) { r.Packed(); r.U32(); if (r.U8() != 0) r.Packed(); else { r.FString(); r.I32(); } }
                }
                uint ng = r.Packed();
                for (uint i = 0; i < ng; i++)
                {
                    int sz = r.I32();
                    if (shown == 0 && (i < 12 || i == ng - 1))
                    {
                        var hb = new StringBuilder();
                        for (int k = 0; k < Math.Min(sz, 40); k++) hb.Append(d[r.Pos + k].ToString("x2")).Append(' ');
                        string txt = Encoding.ASCII.GetString(d, r.Pos, sz).Replace('\0', '.');
                        o.WriteLine("  export {0} size {1}: {2} | {3}", i, sz, hb, new string(Array.FindAll(txt.ToCharArray(), ch => ch >= ' ' && ch < 127)));
                    }
                    r.Skip(sz);
                }
                uint nl = r.Packed(); var lv2 = new List<string>(); for (uint i = 0; i < nl; i++) lv2.Add(r.FString());
                ulong ext = r.U64();
                int ne = 0; while (true) { uint nb = r.Packed(); if (nb == 0) break; r.Packed(); r.Skip((nb + 7) >> 3); ne++; }
                int np = 0; long pb = 0; var seen = new Dictionary<uint, int>();
                while (true) { uint sl = r.Packed(); int sz = r.I32(); if (sz == 0) break; r.Skip(sz); np++; pb += sz; int q; seen.TryGetValue(sl, out q); seen[sl] = q + 1; }
                var sb = new StringBuilder(); foreach (var kv in seen) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(' ');
                o.WriteLine("chunk@{0} t={1:0.000} level={2} nfe={3} guids={4} levels={5} [{6}] extOff={7} ext={8} packets={9} ({10} B) seenLevel {11}",
                    c.Off, t, level, nfe, ng, nl, string.Join(";", lv2.ToArray()), ext, ne, np, pb, sb);
                p = r.Pos; shown++;
            }
            if (shown >= maxFrames) break;
        }
    }

    // Thống kê cờ bunch đầu tiên của mỗi kênh trong frame đầu tiên của file
    public static void BunchStats(string file, TextWriter o)
    {
        byte[] d = ReadShared(file);
        int he, lo, lv, ts;
        var cs = ReadChunks(d, out he, out lo, out lv, out ts);
        Chunk c = cs.Find(x => x.Type == 1);
        int end = c.Start + c.Size;
        var r = new ByteReader(d, c.DataStart, end);
        r.I32(); r.F32();
        uint n = r.Packed();
        for (uint i = 0; i < n; i++) { r.Packed(); if (r.Packed() != 0) { r.FString(); r.Packed(); } if (r.U8() != 0) { r.Packed(); r.U32(); if (r.U8() != 0) r.Packed(); else { r.FString(); r.I32(); } } }
        n = r.Packed(); for (uint i = 0; i < n; i++) r.Skip(r.I32());
        n = r.Packed(); for (uint i = 0; i < n; i++) r.FString();
        r.U64();
        while (true) { uint nb = r.Packed(); if (nb == 0) break; r.Packed(); r.Skip((nb + 7) >> 3); }
        var first = new Dictionary<string, int>(); var all = new Dictionary<string, int>();
        var seen = new HashSet<uint>(); int shown = 0;
        while (true)
        {
            r.Packed(); int sz = r.I32(); if (sz == 0) break;
            byte[] pkt = r.Bytes(sz);
            byte last = pkt[pkt.Length - 1]; if (last == 0) continue;
            long bits = pkt.Length * 8 - 1; while ((last & 0x80) == 0) { last <<= 1; bits--; }
            var b = new BitReader(pkt, bits);
            try
            {
                while (!b.AtEnd)
                {
                    bool control = b.Bit(); bool open = control && b.Bit(); bool close = control && b.Bit();
                    if (close) b.SerInt(15);
                    bool paused = b.Bit(); bool rel = b.Bit(); uint ch = b.Packed();
                    bool ex = b.Bit(), mm = b.Bit(), part = b.Bit(); bool pi = false, pf = false;
                    if (part) { pi = b.Bit(); b.Bit(); pf = b.Bit(); }
                    string name = "";
                    if (rel || open) { if (b.Bit()) name = "#" + b.Packed(); else { name = b.FString(); b.I32(); } }
                    uint nbits = b.SerInt(16384); b.Sub(nbits);
                    string key = string.Format("ctl{0} open{1} close{2} rel{3} part{4}{5}{6} paused{7}", control ? 1 : 0, open ? 1 : 0, close ? 1 : 0, rel ? 1 : 0, part ? 1 : 0, pi ? "i" : "", pf ? "f" : "", paused ? 1 : 0);
                    int q; all.TryGetValue(key, out q); all[key] = q + 1;
                    if (seen.Add(ch)) { first.TryGetValue(key, out q); first[key] = q + 1; if (shown++ < 8) o.WriteLine("  ch {0} {1} name {2}", ch, key, name); }
                }
            }
            catch (Exception) { }
        }
        // các frame sau (5000 frame): bunch tới kênh chưa mở trong frame đầu
        var missing = new Dictionary<uint, int>(); int frames = 0;
        var openedLater = new HashSet<uint>();
        int p2 = r.Pos;
        foreach (var c2 in cs)
        {
            if (c2.Type != 1) continue;
            int e2 = c2.Start + c2.Size;
            int q2 = c2 == c ? p2 : c2.DataStart;
            while (q2 < e2 && frames < 5000)
            {
                int nx = SkipFrame(d, q2, e2);
                var fr = new ByteReader(d, q2, nx);
                fr.I32(); fr.F32();
                uint k1 = fr.Packed(); for (uint i = 0; i < k1; i++) { fr.Packed(); if (fr.Packed() != 0) { fr.FString(); fr.Packed(); } if (fr.U8() != 0) { fr.Packed(); fr.U32(); if (fr.U8() != 0) fr.Packed(); else { fr.FString(); fr.I32(); } } }
                k1 = fr.Packed(); for (uint i = 0; i < k1; i++) fr.Skip(fr.I32());
                k1 = fr.Packed(); for (uint i = 0; i < k1; i++) fr.FString();
                fr.U64();
                while (true) { uint nb = fr.Packed(); if (nb == 0) break; fr.Packed(); fr.Skip((nb + 7) >> 3); }
                while (true)
                {
                    fr.Packed(); int sz = fr.I32(); if (sz == 0) break;
                    byte[] pkt = fr.Bytes(sz);
                    byte last = pkt[pkt.Length - 1]; if (last == 0) continue;
                    long bits = pkt.Length * 8 - 1; while ((last & 0x80) == 0) { last <<= 1; bits--; }
                    var b = new BitReader(pkt, bits);
                    try
                    {
                        while (!b.AtEnd)
                        {
                            bool control = b.Bit(); bool open = control && b.Bit(); bool close = control && b.Bit();
                            if (close) b.SerInt(15);
                            b.Bit(); bool rel = b.Bit(); uint ch = b.Packed();
                            b.Bit(); b.Bit(); bool part = b.Bit(); if (part) { b.Bit(); b.Bit(); b.Bit(); }
                            if (rel || open) { if (b.Bit()) b.Packed(); else { b.FString(); b.I32(); } }
                            uint nbits = b.SerInt(16384); b.Sub(nbits);
                            if (open) openedLater.Add(ch);
                            else if (!seen.Contains(ch) && !openedLater.Contains(ch)) { int q; missing.TryGetValue(ch, out q); missing[ch] = q + 1; }
                        }
                    }
                    catch (Exception) { }
                }
                q2 = nx; frames++;
            }
            if (frames >= 5000) break;
        }
        var ms = new List<uint>(missing.Keys); ms.Sort();
        o.WriteLine("kênh mở trong frame đầu: {0}; kênh nhận dữ liệu khi chưa mở (5000 frame sau): {1} kênh, {2} bunch; vd: {3}",
            seen.Count, missing.Count, missing.Values.Sum(), string.Join(",", ms.Take(40).Select(x => x.ToString()).ToArray()));
        o.WriteLine("bunch đầu tiên của mỗi kênh:"); foreach (var kv in first) o.WriteLine("  {0,6} {1}", kv.Value, kv.Key);
        o.WriteLine("tất cả bunch:"); foreach (var kv in all) o.WriteLine("  {0,6} {1}", kv.Value, kv.Key);
    }

    public static void DumpCheckpoint(string file, int index, TextWriter o)
    {
        byte[] d = ReadShared(file);
        int he, lo, lv, ts;
        var cs = ReadChunks(d, out he, out lo, out lv, out ts);
        Chunk cp = null; int k = 0;
        foreach (var c in cs) if (c.Type == 2 && k++ == index) { cp = c; break; }
        int ds = cp.DataStart, end = cp.Start + cp.Size;
        long po = BitConverter.ToInt64(d, ds);
        o.WriteLine("{0} t={1} dataStart={2} size={3} packetOffset={4} frameAt(rel)={5}", cp.Id, cp.T1, ds, end - ds, po, 8 + po);
        var r = new ByteReader(d, ds + 8, end);
        o.WriteLine("level {0}", r.I32());
        int nDel = r.I32(); o.WriteLine("deleted {0}", nDel);
        for (int i = 0; i < nDel; i++) { string s = r.FString(); if (i < 3) o.WriteLine("  " + s); }
        o.WriteLine("after deleted rel={0}", r.Pos - ds);
        var sb = new StringBuilder();
        for (int i = 0; i < 160; i++) sb.Append(d[r.Pos + i].ToString("x2")).Append(i % 32 == 31 ? "\n" : " ");
        o.WriteLine(sb);
    }

    // Gộp các chunk dữ liệu nhỏ liền nhau thành chunk lớn (≤ maxMs, luôn ngắt ở checkpoint/sự kiện),
    // giống file quay bằng thông số mặc định -> máy chưa chỉnh localReplay.* vẫn xem mượt.
    // Mỗi chunk chứa trọn frame nên chỉ cần nối dữ liệu. Trả về mô tả ngắn.
    public static string Merge(string file, string outFile, uint maxMs)
    {
        byte[] d = ReadShared(file);
        int he, lenOff, liveOff, tsOff;
        ReadChunks(d, out he, out lenOff, out liveOff, out tsOff);
        if (BitConverter.ToUInt32(d, liveOff) != 0) throw new Exception("file đang được quay - hãy dừng quay trước");
        int nIn, nOut;
        byte[] res = MergeData(d, maxMs, out nIn, out nOut);
        File.WriteAllBytes(outFile, res);
        try { File.SetLastWriteTime(outFile, File.GetLastWriteTime(file)); } catch { }
        return string.Format("{0} chunk dữ liệu → {1} chunk, {2:0.0} MB", nIn, nOut, res.Length / 1048576.0);
    }

    static byte[] MergeData(byte[] d, uint maxMs, out int chunksIn, out int chunksOut)
    {
        int he, lenOff, liveOff, tsOff;
        var cs = ReadChunks(d, out he, out lenOff, out liveOff, out tsOff);
        var o = new MemoryStream();
        o.Write(d, 0, he);
        var group = new List<Chunk>();
        int nIn = 0, nOut = 0;
        Action flush = () =>
        {
            if (group.Count == 0) return;
            long total = 0;
            foreach (var g in group) total += g.Start + g.Size - g.DataStart;
            WI32(o, 1); WI32(o, (int)(16 + total));
            o.Write(BitConverter.GetBytes(group[0].T1), 0, 4);
            o.Write(BitConverter.GetBytes(group[group.Count - 1].T2), 0, 4);
            WI32(o, (int)total); WI32(o, (int)total);   // không nén: kích thước bộ nhớ = kích thước dữ liệu
            foreach (var g in group) o.Write(d, g.DataStart, g.Start + g.Size - g.DataStart);
            group.Clear(); nOut++;
        };
        foreach (var c in cs)
        {
            if (c.Type == 1)
            {
                nIn++;
                uint len = BitConverter.ToUInt32(d, c.Start + 8), mem = BitConverter.ToUInt32(d, c.Start + 12);
                if (len != c.Size - 16 || mem != len) throw new Exception("chunk dữ liệu có cấu trúc lạ (bị nén?) tại " + c.Off);
                if (group.Count > 0 && c.T2 - group[0].T1 > maxMs) flush();
                group.Add(c);
                continue;
            }
            flush();                      // checkpoint / sự kiện / header: giữ nguyên, làm ranh giới
            o.Write(d, c.Off, 8 + c.Size);
        }
        flush();
        chunksIn = nIn; chunksOut = nOut;
        return o.ToArray();
    }

    // "52" (phút), "52:30" (phút:giây), "1:02:30" (giờ:phút:giây) -> mili giây; trống -> -1
    public static long ParseTime(string s)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        if (s == "") return -1;
        var parts = s.Split(':');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double v;
        if (parts.Length == 1)
        {
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out v) || v < 0) throw new FormatException(s);
            return (long)(v * 60000);
        }
        if (parts.Length > 3) throw new FormatException(s);
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, inv, out v) || v < 0 || (i > 0 && v >= 60)) throw new FormatException(s);
            total = total * 60 + v;
        }
        return (long)(total * 1000);
    }

    public static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
    }

    // Tên file cắt, đặt cạnh file gốc: demo5_cat_52-00_den_het.replay
    public static string DefaultCutName(string file, long fromMs, long toMs)
    {
        Func<long, string> f = ms => FormatTime(ms).Replace(':', '-');
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)),
            Path.GetFileNameWithoutExtension(file) + "_cat_" + f(fromMs) + "_den_" + (toMs > 0 ? f(toMs) : "het") + ".replay");
    }

    // Độ dài replay (ms) theo header
    public static long LengthMs(string file)
    {
        byte[] b = new byte[4096];
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) fs.Read(b, 0, b.Length);
        if (BitConverter.ToUInt32(b, 0) != 0x1CA2E27F) throw new Exception("không phải file replay");
        int ncv = BitConverter.ToInt32(b, 8);
        return BitConverter.ToUInt32(b, 12 + 20 * ncv);
    }

    static byte[] ReadShared(string file)
    {
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var d = new byte[fs.Length]; int off = 0, n;
            while (off < d.Length && (n = fs.Read(d, off, d.Length - off)) > 0) off += n;
            return d;
        }
    }
}
