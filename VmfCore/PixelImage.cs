namespace VmfCore;

/// <summary>Dekodierte Textur (RGBA, Zeile 0 = oben) aus einer .2db (DXT1 0x84, DXT3 0x86, DXT5 0x88).</summary>
public sealed class PixelImage
{
    public int Width, Height;
    public byte[] Rgba = Array.Empty<byte>();   // 4 Byte je Pixel

    public Rgba Sample(float x, float y)
    {
        int ix = Math.Clamp((int)x, 0, Width - 1), iy = Math.Clamp((int)y, 0, Height - 1), o = (iy * Width + ix) * 4;
        return new Rgba(Rgba[o], Rgba[o + 1], Rgba[o + 2], Rgba[o + 3]);
    }

    public const int DataOffset = 0x50;

    public static PixelImage Decode2db(byte[] d)
    {
        if (d == null || d.Length < DataOffset || !Bin.TagIs(d, 0x10, "BMAP")) return null;
        int w = Bin.U16(d, 0x28), h = Bin.U16(d, 0x2A), fmt = (int)Bin.U32(d, 0x30) & ~0x40;
        if (w < 4 || h < 4 || (fmt != 0x84 && fmt != 0x86 && fmt != 0x88)) return null;
        int bw = w / 4, bh = h / 4, bb = fmt == 0x84 ? 8 : 16, o = DataOffset;
        if (o + bw * bh * bb > d.Length) return null;
        var img = new PixelImage { Width = w, Height = h, Rgba = new byte[w * h * 4] };
        var pal = new Rgba[4]; var alpha = new byte[8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = o + (by * bw + bx) * bb, c = fmt == 0x84 ? b : b + 8;
                ushort c0 = Bin.U16(d, c), c1 = Bin.U16(d, c + 2);
                pal[0] = C565(c0); pal[1] = C565(c1);
                if (c0 > c1 || fmt != 0x84) { pal[2] = Mix(pal[0], pal[1], 2, 1); pal[3] = Mix(pal[0], pal[1], 1, 2); }
                else { pal[2] = Mix(pal[0], pal[1], 1, 1); pal[3] = new Rgba(0, 0, 0, 0); }
                uint bits = Bin.U32(d, c + 4);
                ulong abits = 0;
                if (fmt == 0x88)
                {
                    alpha[0] = d[b]; alpha[1] = d[b + 1];
                    if (alpha[0] > alpha[1]) for (int i = 2; i < 8; i++) alpha[i] = (byte)(((8 - i) * alpha[0] + (i - 1) * alpha[1]) / 7);
                    else { for (int i = 2; i < 6; i++) alpha[i] = (byte)(((6 - i) * alpha[0] + (i - 1) * alpha[1]) / 5); alpha[6] = 0; alpha[7] = 255; }
                    for (int i = 0; i < 6; i++) abits |= (ulong)d[b + 2 + i] << (8 * i);
                }
                for (int i = 0; i < 16; i++)
                {
                    var px = pal[(bits >> (2 * i)) & 3];
                    if (fmt == 0x88) px.A = alpha[(abits >> (3 * i)) & 7];
                    else if (fmt == 0x86) { int nib = (d[b + i / 2] >> ((i & 1) * 4)) & 15; px.A = (byte)(nib * 17); }
                    int po = ((by * 4 + (i >> 2)) * w + bx * 4 + (i & 3)) * 4;
                    img.Rgba[po] = px.R; img.Rgba[po + 1] = px.G; img.Rgba[po + 2] = px.B; img.Rgba[po + 3] = px.A;
                }
            }
        return img;
    }

    static Rgba C565(ushort c) => new((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), 255);
    static Rgba Mix(Rgba a, Rgba b, int wa, int wb)
    {
        int s = wa + wb;
        return new((byte)((a.R * wa + b.R * wb) / s), (byte)((a.G * wa + b.G * wb) / s), (byte)((a.B * wa + b.B * wb) / s), 255);
    }
}
