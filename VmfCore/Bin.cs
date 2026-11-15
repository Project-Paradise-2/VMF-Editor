using System.Text;

namespace VmfCore;

/// <summary>Little-Endian-Lesen/Schreiben.</summary>
public static class Bin
{
    public static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    public static short S16(byte[] d, int o) => (short)(d[o] | (d[o + 1] << 8));
    public static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    public static int S32(byte[] d, int o) => (int)U32(d, o);
    public static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);
    public static void W16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    public static void W32(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
    public static string Latin1(byte[] d, int o, int n) => Encoding.Latin1.GetString(d, o, n);
    public static bool TagIs(byte[] d, int o, string tag)
    {
        if (o + tag.Length > d.Length) return false;
        for (int i = 0; i < tag.Length; i++) if (d[o + i] != tag[i]) return false;
        return true;
    }

    /// <summary>Nullterminierte Zeichenkette (Bytes als Latin-1 wie im Flash-Player des Spiels).</summary>
    public static string CString(byte[] d, ref int q, int end)
    {
        int s = q; while (q < end && d[q] != 0) q++;
        string r = Encoding.Latin1.GetString(d, s, q - s);
        if (q < end) q++;
        return r;
    }
}
