using System;
using System.Collections.Generic;

class Bunch
{
    public bool Control, Open, Close, Reliable, Partial, PartialInitial, PartialFinal, HasExports, HasMustBeMapped;
    public uint ChIndex; public string ChName; public BitReader Ar;
}

class Channel
{
    public uint Index; public string Name; public uint ActorGuid; public uint Archetype;
    public BitReader PartialAr; public bool Broken; public bool PartialOpen, PartialMustMap;
}

partial class Replay
{
    // Tuỳ chọn thử nghiệm định dạng
    public uint MaxPacketBits = 16384;
    public bool PartialCustomExportsBit = true;

    public int BunchCount, PacketErrors, PacketsOk;
    public Dictionary<uint, Channel> Channels = new Dictionary<uint, Channel>();

    void OnPacket(byte[] pkt)
    {
        byte last = pkt[pkt.Length - 1];
        if (last == 0) { PacketErrors++; return; }
        long bits = pkt.Length * 8 - 1;
        while ((last & 0x80) == 0) { last <<= 1; bits--; }
        var b = new BitReader(pkt, bits);
        try
        {
            while (!b.AtEnd)
            {
                var bunch = ReadBunchHeader(b);
                BunchCount++;
                OnBunch(bunch);
            }
            if (b.Pos != b.End) throw new BitError("overrun");
            PacketsOk++;
        }
        catch (BitError e)
        {
            PacketErrors++;
            if (PacketErrors <= 5) Log.WriteLine("packet error: " + e.Message + " at " + b.Pos + "/" + b.End);
        }
    }

    Bunch ReadBunchHeader(BitReader b)
    {
        var u = new Bunch();
        u.Control = b.Bit();
        u.Open = u.Control && b.Bit();
        u.Close = u.Control && b.Bit();
        if (u.Close) b.SerInt(15); // close reason
        b.Bit(); // replication paused
        u.Reliable = b.Bit();
        u.ChIndex = b.Packed();
        u.HasExports = b.Bit();
        u.HasMustBeMapped = b.Bit();
        u.Partial = b.Bit();
        if (u.Partial)
        {
            u.PartialInitial = b.Bit();
            if (PartialCustomExportsBit) b.Bit();
            u.PartialFinal = b.Bit();
        }
        if (u.Reliable || u.Open)
        {
            if (b.Bit()) u.ChName = "#" + b.Packed();
            else { u.ChName = b.FString(); b.I32(); }
        }
        uint nbits = b.SerInt(MaxPacketBits);
        u.Ar = b.Sub(nbits);
        return u;
    }
}
