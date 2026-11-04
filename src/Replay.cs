using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

class ExportGroup
{
    public string Path; public uint NumExports; public Dictionary<uint, string> Fields = new Dictionary<uint, string>();
}

partial class Replay
{
    public byte[] D;
    public Dictionary<uint, ExportGroup> GroupByIndex = new Dictionary<uint, ExportGroup>();
    public Dictionary<string, ExportGroup> GroupByPath = new Dictionary<string, ExportGroup>();
    public Dictionary<uint, string> GuidPath = new Dictionary<uint, string>();
    public Dictionary<uint, uint> GuidOuter = new Dictionary<uint, uint>();
    public TextWriter Log;
    public int PacketCount, FrameCount;
    public float CurTime;

    public void ParseFile(string path)
    {
        LoadFile(path);
        ParseMore();
    }

    // Đọc lại file từ đĩa (chỉ đọc, cho phép game vẫn đang ghi) và cập nhật thông tin header.
    // Dùng được nhiều lần trên cùng một đối tượng: các frame đã xử lý không bị đọc lại.
    // Replay đang quay: GIỮ file mở giữa các lần đọc. Mở lại file vừa được game ghi làm Windows Defender quét lại
    // (~15 ms mỗi lần, ~7 lần/giây). Đóng khi game quay xong (bIsLive = 0), khi đổi file, hoặc CloseFile().
    FileStream liveFs; string liveFsPath;
    public void CloseFile()
    {
        if (liveFs != null) { try { liveFs.Dispose(); } catch { } }
        liveFs = null; liveFsPath = null;
    }

    public void LoadFile(string path)
    {
        if (liveFs != null && !string.Equals(liveFsPath, path, StringComparison.OrdinalIgnoreCase)) CloseFile();
        if (liveFs != null)
        {
            // file ở đường dẫn này nhỏ hơn file đang giữ = file khác (bị xoá rồi tạo lại) -> mở lại
            long held; try { held = liveFs.Length; } catch { held = -1; }
            FileInfo fi = new FileInfo(path);
            if (held < 0 || !fi.Exists || fi.Length < held) CloseFile();
        }
        if (liveFs == null)
        {
            // bufferSize 1 = không đệm: luôn đọc dữ liệu mới nhất game vừa ghi
            liveFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            liveFsPath = path;
        }
        try { ReadFileData(liveFs); }
        catch { CloseFile(); throw; }
        if (!IsLive) CloseFile(); // quay xong: file không đổi nữa, không giữ khoá
    }

    void ReadFileData(FileStream fs)
    {
        {
            int len = (int)fs.Length;
            if (D == null || resumeChunk < 0 || len < DLen)
            {
                // lần đầu (hoặc file bị ghi lại từ đầu): đọc cả file
                D = new byte[len + (1 << 20)];
                ReadInto(fs, 0, len);
            }
            else
            {
                // các lần sau: phần trước chunk đang dở đã cố định -> chỉ đọc lại header file
                // (game cập nhật độ dài / cờ "đang quay") và từ chunk đang dở tới cuối file
                if (len > D.Length)
                {
                    var bigger = new byte[Math.Max(len + (1 << 20), D.Length * 2)];
                    Buffer.BlockCopy(D, 0, bigger, 0, DLen);
                    D = bigger;
                }
                ReadInto(fs, 0, Math.Min(headerEnd, len));
                // đầu chunk đang dở (game cập nhật kích thước ở đây) + phần sau frame cuối đã xử lý
                ReadInto(fs, resumeChunk, Math.Min(resumeChunk + 24, len));
                ReadInto(fs, Math.Max(resumeChunk + 24, resumeFrame), len);
            }
            DLen = len;
        }
        var r = new ByteReader(D, 0, DLen);
        if (r.U32() != 0x1CA2E27F) throw new Exception("not a replay");
        uint ver = r.U32();
        int ncv = r.I32(); r.Pos += 20 * ncv;
        uint lenMs = r.U32(); r.U32(); r.U32();
        r.FString();
        IsLive = r.U32() != 0; // game vẫn đang quay vào file này
        long ts = BitConverter.ToInt64(D, r.Pos); r.Pos += 8;
        uint compressed = r.U32(); uint encrypted = r.U32(); int kl = r.I32(); r.Pos += kl;
        if (resumeChunk < 0)
        {
            Log.WriteLine("# length {0}s, start {1}, compressed {2}", lenMs / 1000.0, new DateTime(ts), compressed);
            resumeChunk = r.Pos;
            headerEnd = r.Pos;
        }
        StartTime = new DateTime(ts); LengthSec = Math.Max(lenMs / 1000.0, MaxTime);
        if (compressed != 0 || encrypted != 0) throw new Exception("replay bị nén/mã hoá - chưa hỗ trợ");
    }

    // Vị trí đọc tiếp theo: chunk đang dở và frame kế tiếp trong chunk đó (-1 = đầu chunk)
    int resumeChunk = -1, resumeFrame = -1;
    // D có thể lớn hơn dữ liệu thật (để chừa chỗ ghi thêm): số byte thật là DLen
    public int DLen; int headerEnd;

    void ReadInto(FileStream fs, int from, int to)
    {
        fs.Position = from;
        int off = from, n;
        while (off < to && (n = fs.Read(D, off, to - off)) > 0) off += n;
    }
    public bool IsLive; public double MaxTime;

    // Xử lý mọi frame mới có trong D kể từ lần trước. Frame chưa ghi xong thì để lần sau.
    public void ParseMore()
    {
        int pos = resumeChunk;
        while (pos + 8 <= DLen)
        {
            uint type = BitConverter.ToUInt32(D, pos); int size = BitConverter.ToInt32(D, pos + 4);
            int start = pos + 8;
            bool complete = size >= 0 && (long)start + size <= DLen;
            // chunk đã đóng: đã có chunk tiếp theo phía sau, hoặc game đã quay xong
            bool closed = complete && ((long)start + size + 8 <= DLen || !IsLive);
            if (type == 0 && complete && MapName == "")
            {
                var m = System.Text.RegularExpressions.Regex.Match(Encoding.ASCII.GetString(D, start, size), @"/Game/[ -~]*?/Maps/[ -~]+");
                if (m.Success) MapName = m.Value.Substring(m.Value.LastIndexOf('/') + 1);
            }
            if (type == 1)
            {
                bool allDone = ParseDataChunk(start, complete ? start + size : DLen);
                if (!allDone || !closed) { resumeChunk = pos; return; } // chunk còn đang được ghi thêm
            }
            else if (!closed) { resumeChunk = pos; return; }
            pos = start + size; resumeFrame = -1;
        }
        resumeChunk = pos;
    }

    // Đọc các frame trong 1 chunk dữ liệu. Trả về false nếu gặp frame chưa ghi xong.
    bool ParseDataChunk(int start, int end)
    {
        if (start + 16 > end) return false;
        uint t0 = BitConverter.ToUInt32(D, start), t1 = BitConverter.ToUInt32(D, start + 4);
        uint len = BitConverter.ToUInt32(D, start + 8);
        int dataStart = start + 16;
        int dataEnd = (int)Math.Min(end, (long)dataStart + len);
        var r = new ByteReader(D, resumeFrame >= 0 ? resumeFrame : dataStart, dataEnd);
        while (!r.AtEnd)
        {
            // kiểm tra frame đã đủ dữ liệu chưa TRƯỚC khi xử lý (tránh xử lý dở rồi đọc lại -> trùng sự kiện)
            if (!FrameComplete(r.Pos, dataEnd)) { resumeFrame = r.Pos; return false; }
            ReadFrame(r); FrameCount++;
            if (CurTime > MaxTime) MaxTime = CurTime;
        }
        resumeFrame = r.Pos;
        LengthSec = Math.Max(LengthSec, MaxTime);
        return true;
    }

    // Đi qua cấu trúc 1 frame mà không xử lý gì, chỉ để biết frame đã ghi đủ chưa
    bool FrameComplete(int pos, int end)
    {
        var r = new ByteReader(D, pos, end);
        try
        {
            r.I32(); r.F32();
            uint n = r.Packed();                       // net field exports
            for (uint i = 0; i < n; i++)
            {
                r.Packed();
                if (r.Packed() != 0) { r.FString(); r.Packed(); }
                if (r.U8() != 0) { r.Packed(); r.U32(); if (r.U8() != 0) r.Packed(); else { r.FString(); r.I32(); } }
            }
            n = r.Packed();                            // net export guids
            for (uint i = 0; i < n; i++) r.Skip(r.I32());
            n = r.Packed();                            // streaming levels
            for (uint i = 0; i < n; i++) r.FString();
            r.U64();
            while (true) { uint nb = r.Packed(); if (nb == 0) break; r.Packed(); r.Skip((nb + 7) >> 3); }   // external data
            while (true) { r.Packed(); int sz = r.I32(); if (sz == 0) break; r.Skip(sz); }                   // packets
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    void ReadFrame(ByteReader r)
    {
        int level = r.I32();
        CurTime = r.F32();
        ReadNetFieldExports(r);
        ReadNetExportGuids(r);
        uint nLevels = r.Packed();
        for (int i = 0; i < nLevels; i++) r.FString();
        r.U64(); // external offset
        // external data
        while (true)
        {
            uint nbits = r.Packed();
            if (nbits == 0) break;
            uint guid = r.Packed();
            byte[] ext = r.Bytes((int)((nbits + 7) >> 3));
            OnExternalData(guid, ext, nbits);
        }
        while (true)
        {
            uint seenLevel = r.Packed();
            int size = r.I32();
            if (size == 0) break;
            byte[] pkt = r.Bytes(size);
            PacketCount++;
            OnPacket(pkt);
        }
    }

    void ReadNetFieldExports(ByteReader r)
    {
        uint n = r.Packed();
        for (int i = 0; i < n; i++)
        {
            uint pathIdx = r.Packed();
            uint exported = r.Packed();
            ExportGroup g;
            if (exported != 0)
            {
                string path = r.FString();
                uint num = r.Packed();
                if (!GroupByIndex.TryGetValue(pathIdx, out g))
                {
                    g = new ExportGroup { Path = path, NumExports = num };
                    GroupByIndex[pathIdx] = g; GroupByPath[path] = g;
                }
            }
            else g = GroupByIndex[pathIdx];
            byte flags = r.U8();
            if (flags != 0)
            {
                uint handle = r.Packed();
                uint checksum = r.U32();
                string name = ReadFNameBytes(r);
                g.Fields[handle] = name;
            }
        }
    }

    string ReadFNameBytes(ByteReader r)
    {
        byte hard = r.U8();
        if (hard != 0) return "#" + r.Packed();
        string s = r.FString(); r.I32();
        return s;
    }

    void ReadNetExportGuids(ByteReader r)
    {
        uint n = r.Packed();
        for (int i = 0; i < n; i++)
        {
            int size = r.I32();
            byte[] b = r.Bytes(size);
            var br = new BitReader(b, size * 8);
            LoadObject(br, true);
        }
    }

    // InternalLoadObject
    public uint LoadObject(BitReader b, bool exporting)
    {
        uint guid = b.Packed();
        if (guid == 0) return 0;
        if (guid == 1 || exporting)
        {
            byte flags = b.U8();
            if ((flags & 1) != 0)
            {
                uint outer = LoadObject(b, true);
                string path = b.FString();
                if ((flags & 4) != 0) b.U32();
                if (exporting) { GuidPath[guid] = path; GuidOuter[guid] = outer; }
            }
        }
        return guid;
    }

    public string FullPath(uint guid)
    {
        string p; if (!GuidPath.TryGetValue(guid, out p)) return null;
        uint o; if (GuidOuter.TryGetValue(guid, out o) && o != 0) { string op = FullPath(o); if (op != null) return op + "." + p; }
        return p;
    }
}
