namespace FrameCastStudio.Core.Streaming;

public static class FlvMuxer
{
    public static byte[] BuildAvcSequenceHeader(byte[] sps, byte[] pps)
    {
        var m = new MemoryStream();
        m.Write(new byte[] { 0x17, 0x00, 0, 0, 0 });
        m.Write(new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1 });
        m.WriteByte((byte)(sps.Length >> 8)); m.WriteByte((byte)sps.Length); m.Write(sps);
        m.WriteByte(1); m.WriteByte((byte)(pps.Length >> 8)); m.WriteByte((byte)pps.Length); m.Write(pps);
        return m.ToArray();
    }

    public static byte[] BuildVideoTag(ReadOnlySpan<byte> annexB, bool key)
    {
        var m = new MemoryStream();
        m.Write(new byte[] { (byte)(key ? 0x17 : 0x27), 0x01, 0, 0, 0 });
        foreach (var nal in SplitNals(annexB))
        {
            int type = nal[0] & 0x1F;
            if (type is 7 or 8 or 9) continue;
            m.WriteByte((byte)(nal.Length >> 24)); m.WriteByte((byte)(nal.Length >> 16));
            m.WriteByte((byte)(nal.Length >> 8));  m.WriteByte((byte)nal.Length);
            m.Write(nal);
        }
        return m.ToArray();
    }

    public static byte[] BuildAacTag(ReadOnlySpan<byte> raw, bool header)
    {
        var b = new byte[2 + raw.Length];
        b[0] = 0xAF; b[1] = (byte)(header ? 0 : 1);
        raw.CopyTo(b.AsSpan(2)); return b;
    }

    public static IEnumerable<byte[]> SplitNals(ReadOnlySpan<byte> d)
    {
        var list = new List<byte[]>(); int i = 0, start = -1;
        while (i + 3 <= d.Length)
        {
            if (d[i] == 0 && d[i + 1] == 0 && (d[i + 2] == 1 || (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1)))
            {
                if (start >= 0) list.Add(d[start..i].ToArray());
                i += d[i + 2] == 1 ? 3 : 4; start = i;
            }
            else i++;
        }
        if (start >= 0 && start < d.Length) list.Add(d[start..].ToArray());
        return list;
    }

    public static (byte[] sps, byte[] pps)? ExtractParams(ReadOnlySpan<byte> annexB)
    {
        byte[]? sps = null, pps = null;
        foreach (var n in SplitNals(annexB)) { if ((n[0] & 0x1F) == 7) sps = n; else if ((n[0] & 0x1F) == 8) pps = n; }
        return sps != null && pps != null ? (sps, pps) : null;
    }
}
