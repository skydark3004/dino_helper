using System;
using System.IO;
using System.Text;

// Đọc byte (archive cấp frame)
class ByteReader
{
    public byte[] D; public int Pos; public int End;
    public ByteReader(byte[] d, int start, int end) { D = d; Pos = start; End = end; }
    public bool AtEnd { get { return Pos >= End; } }
    // đọc quá phần dữ liệu đang có (file đang được game ghi dở) -> báo "chưa đủ dữ liệu"
    void Need(long n) { if (n < 0 || Pos + n > End) throw new EndOfStreamException(); }
    public byte U8() { Need(1); return D[Pos++]; }
    public uint U32() { Need(4); uint v = BitConverter.ToUInt32(D, Pos); Pos += 4; return v; }
    public int I32() { Need(4); int v = BitConverter.ToInt32(D, Pos); Pos += 4; return v; }
    public ulong U64() { Need(8); ulong v = BitConverter.ToUInt64(D, Pos); Pos += 8; return v; }
    public float F32() { Need(4); float v = BitConverter.ToSingle(D, Pos); Pos += 4; return v; }
    public uint Packed()
    {
        uint v = 0; int cnt = 0;
        while (true) { byte b = U8(); v |= (uint)(b >> 1) << (7 * cnt++); if ((b & 1) == 0) break; }
        return v;
    }
    public string FString()
    {
        int len = I32();
        if (len == 0) return "";
        string s;
        if (len < 0) { Need(-(long)len * 2); s = Encoding.Unicode.GetString(D, Pos, -len * 2); Pos += -len * 2; }
        else { Need(len); s = Encoding.ASCII.GetString(D, Pos, len); Pos += len; }
        return s.TrimEnd('\0');
    }
    public byte[] Bytes(int n) { Need(n); byte[] r = new byte[n]; Buffer.BlockCopy(D, Pos, r, 0, n); Pos += n; return r; }
    public void Skip(long n) { Need(n); Pos += (int)n; }
}

class BitError : Exception { public BitError(string m) : base(m) { } }

// Đọc bit, LSB trước (giống FBitReader của Unreal)
class BitReader
{
    public byte[] D; public long Pos; public long End;
    public BitReader(byte[] d, long bits) { D = d; Pos = 0; End = bits; }
    public bool AtEnd { get { return Pos >= End; } }
    public long Left { get { return End - Pos; } }
    // Chế độ "thử": đọc quá cuối thì đánh dấu Failed và trả 0 thay vì ném lỗi
    // (ném lỗi trong .NET rất chậm; khi giải mã thử nhiều cách, phần lớn thời gian mất vào đó)
    public bool Soft, Failed;
    bool PastEnd(long n)
    {
        if (Pos + n <= End) return false;
        if (!Soft) throw new BitError("read past end (" + n + ")");
        Failed = true; Pos = End; return true;
    }
    public bool Bit()
    {
        if (PastEnd(1)) return false;
        bool b = ((D[Pos >> 3] >> (int)(Pos & 7)) & 1) != 0; Pos++; return b;
    }
    public ulong Bits(int n)
    {
        if (PastEnd(n)) return 0;
        // đọc theo từng byte (tối đa 8 bit/lần) thay vì từng bit
        ulong v = 0; int got = 0;
        while (got < n)
        {
            int off = (int)(Pos & 7), take = Math.Min(8 - off, n - got);
            v |= (ulong)((D[Pos >> 3] >> off) & ((1 << take) - 1)) << got;
            got += take; Pos += take;
        }
        return v;
    }
    public byte U8() { return (byte)Bits(8); }
    public ushort U16() { return (ushort)Bits(16); }
    public uint U32() { return (uint)Bits(32); }
    public int I32() { return (int)(uint)Bits(32); }
    public float F32() { return BitConverter.ToSingle(BitConverter.GetBytes(U32()), 0); }
    public double F64() { return BitConverter.ToDouble(BitConverter.GetBytes(Bits(64)), 0); }
    public uint SerInt(uint max)
    {
        uint v = 0;
        for (uint mask = 1; (v + mask) < max && mask != 0; mask *= 2) if (Bit()) v |= mask;
        return v;
    }
    public uint Packed()
    {
        uint v = 0; int cnt = 0;
        while (true)
        {
            uint b = U8(); v |= (b >> 1) << (7 * cnt++);
            if ((b & 1) == 0 || Failed) break;
            if (cnt > 5) { if (Soft) { Failed = true; break; } throw new BitError("bad packed"); }
        }
        return v;
    }
    public string FString()
    {
        int len = I32();
        if (len == 0 || Failed) return "";
        if (len > 4096 || len < -4096) { if (Soft) { Failed = true; return ""; } throw new BitError("bad string len " + len); }
        byte[] b;
        string s;
        if (len < 0) { b = BytesOf(-len * 2); s = Encoding.Unicode.GetString(b); }
        else { b = BytesOf(len); s = Encoding.UTF8.GetString(b); }
        return s.TrimEnd('\0');
    }
    public byte[] BytesOf(int n)
    {
        if (PastEnd((long)n * 8)) return new byte[0];
        byte[] r = new byte[n];
        CopyBits(D, Pos, r, 0, (long)n * 8);
        Pos += (long)n * 8;
        return r;
    }
    // Cắt n bit tiếp theo thành reader riêng
    public BitReader Sub(long n)
    {
        if (Pos + n > End) throw new BitError("sub past end");
        byte[] r = new byte[(n + 7) / 8 + 1];
        CopyBits(D, Pos, r, 0, n);
        Pos += n;
        return new BitReader(r, n);
    }
    // Nối thêm bit (gộp partial bunch)
    public static BitReader Concat(BitReader a, BitReader b)
    {
        long n = a.End + b.End;
        byte[] r = new byte[(n + 7) / 8 + 1];
        CopyBits(a.D, 0, r, 0, a.End);
        CopyBits(b.D, 0, r, a.End, b.End);
        return new BitReader(r, n);
    }

    // Chép n bit từ src (bắt đầu ở bit sPos) sang dst (bit dPos), dst phải đang = 0 ở vùng ghi.
    // Chép theo byte: nhanh hơn chép từng bit khoảng 8 lần.
    static void CopyBits(byte[] src, long sPos, byte[] dst, long dPos, long n)
    {
        if ((sPos & 7) == 0 && (dPos & 7) == 0)
        {
            long whole = n >> 3;
            Buffer.BlockCopy(src, (int)(sPos >> 3), dst, (int)(dPos >> 3), (int)whole);
            sPos += whole << 3; dPos += whole << 3; n -= whole << 3;
        }
        while (n > 0)
        {
            int sOff = (int)(sPos & 7), dOff = (int)(dPos & 7);
            int take = (int)Math.Min(n, Math.Min(8 - sOff, 8 - dOff));
            int bits = (src[sPos >> 3] >> sOff) & ((1 << take) - 1);
            dst[dPos >> 3] |= (byte)(bits << dOff);
            sPos += take; dPos += take; n -= take;
        }
    }
}
