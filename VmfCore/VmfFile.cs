namespace VmfCore;

/// <summary>
/// Ein Tag des VMF-Tag-Stroms. Unveränderte Tags werden bytegenau zurückgeschrieben (Body + ursprüngliche Kopfform).
/// Tag 127 (Eden-DefineSprite) hat eigene Kinder: Body = u16 Id, u16 Frames, u32 Länge, Kinder-Tags, ggf. Rest.
/// </summary>
public sealed class VmfTag
{
    public int Code;
    public byte[] Body = Array.Empty<byte>();
    /// <summary>Ursprünglich mit langem Kopf (0x3F + u32) gespeichert – beim Schreiben beibehalten.</summary>
    public bool LongHeader;
    // Sprite (127)
    public ushort SpriteId, SpriteFrames;
    public List<VmfTag> Children;
    public byte[] SpriteTail = Array.Empty<byte>();
    /// <summary>Gespeicherte Stromlänge minus tatsächliche Länge der Kinder (0 im Normalfall) – für bytegleiches Zurückschreiben.</summary>
    public int InnerDelta;

    public bool IsSprite => Code == 127 && Children != null;
    public override string ToString() => $"Tag {Code} ({TagNames.Name(Code)}, {Body.Length} B)";
}

/// <summary>
/// VMF-Datei als Tag-Baum (Format: Docs/TDU2_VMF_Flash_Format.md). Kopf und Ressourcentabelle werden unverändert übernommen,
/// nur die Dateilänge (Offset 6) und – falls geändert – die Frame-Anzahl (Offset 16) neu gesetzt.
/// </summary>
public sealed class VmfFile
{
    public byte[] Header = Array.Empty<byte>();
    public List<VmfTag> Tags = new();
    public byte[] Trailer = Array.Empty<byte>();

    public int SwfVersion => Header.Length > 3 ? Header[3] : 6;
    public int FrameRate => Header.Length > 15 ? Math.Max(1, (Header[14] << 8) | Header[15]) : 30;
    public int RootFrames => Header.Length > 17 ? Bin.U16(Header, 16) : 1;

    public static VmfFile Parse(byte[] d)
    {
        if (d == null || d.Length < 19 || d[0] != 'F' || d[1] != 'M' || d[2] != 'V') throw new InvalidDataException("Not a VMF file (FMV missing)");
        int o = 19;
        if (d[18] != 0) { int n = Bin.U16(d, 19); o = 21 + 4 * n + 2; }
        var f = new VmfFile { Header = d[..o] };
        int end = d.Length;
        f.Tags = ReadTags(d, o, end, out int stop);
        if (stop < end) f.Trailer = d[stop..end];
        return f;
    }

    static List<VmfTag> ReadTags(byte[] d, int o, int end, out int stop)
    {
        var list = new List<VmfTag>();
        while (o + 2 <= end)
        {
            int h = Bin.U16(d, o), code = h >> 6, len = h & 0x3F, p = o + 2;
            bool lng = len == 0x3F;
            if (lng) { if (p + 4 > end) break; len = (int)Bin.U32(d, p); p += 4; }
            if (p + len > end) break;
            var t = new VmfTag { Code = code, LongHeader = lng, Body = d[p..(p + len)] };
            if (code == 127 && len >= 8)
            {
                t.SpriteId = Bin.U16(d, p); t.SpriteFrames = Bin.U16(d, p + 2);
                int inner = (int)Bin.U32(d, p + 4);
                int innerEnd = Math.Min(p + len, p + 8 + inner);
                t.Children = ReadTags(d, p + 8, innerEnd, out int cs);
                // Rest hinter dem eingebetteten Strom (auch unvollständige Tags) bytegleich behalten
                int restStart = Math.Min(cs, innerEnd);
                t.SpriteTail = d[restStart..(p + len)];
                t.InnerDelta = inner - (restStart - (p + 8));
            }
            list.Add(t);
            o = p + len;
            if (code == 0) break;
        }
        stop = o;
        return list;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        ms.Write(Header, 0, Header.Length);
        foreach (var t in Tags) WriteTag(ms, t);
        ms.Write(Trailer, 0, Trailer.Length);
        var r = ms.ToArray();
        Bin.W32(r, 6, (uint)r.Length);
        return r;
    }

    /// <summary>Frame-Anzahl der Hauptzeitleiste neu setzen (nach Einfügen/Löschen von Frames).</summary>
    public void SetRootFrames(int n) { if (Header.Length > 17) Bin.W16(Header, 16, n); }

    static void WriteTag(Stream s, VmfTag t)
    {
        byte[] body = t.Body;
        if (t.IsSprite)
        {
            using var inner = new MemoryStream();
            foreach (var c in t.Children) WriteTag(inner, c);
            var ib = inner.ToArray();
            body = new byte[8 + ib.Length + t.SpriteTail.Length];
            Bin.W16(body, 0, t.SpriteId); Bin.W16(body, 2, t.SpriteFrames);
            Bin.W32(body, 4, (uint)(ib.Length + t.InnerDelta));
            Buffer.BlockCopy(ib, 0, body, 8, ib.Length);
            Buffer.BlockCopy(t.SpriteTail, 0, body, 8 + ib.Length, t.SpriteTail.Length);
            t.Body = body;
        }
        bool lng = t.LongHeader || body.Length >= 0x3F;
        int h = (t.Code << 6) | (lng ? 0x3F : body.Length);
        s.WriteByte((byte)h); s.WriteByte((byte)(h >> 8));
        if (lng) { s.WriteByte((byte)body.Length); s.WriteByte((byte)(body.Length >> 8)); s.WriteByte((byte)(body.Length >> 16)); s.WriteByte((byte)(body.Length >> 24)); }
        s.Write(body, 0, body.Length);
    }

    /// <summary>Alle Tags (rekursiv in Sprites) mit ihrer Liste.</summary>
    public IEnumerable<(VmfTag tag, List<VmfTag> owner)> AllTags()
    {
        foreach (var x in Walk(Tags)) yield return x;
    }

    static IEnumerable<(VmfTag, List<VmfTag>)> Walk(List<VmfTag> list)
    {
        foreach (var t in list)
        {
            yield return (t, list);
            if (t.IsSprite) foreach (var x in Walk(t.Children)) yield return x;
        }
    }
}

public static class TagNames
{
    public static string Name(int code) => code switch
    {
        0 => "End", 1 => "ShowFrame", 2 => "DefineShape", 9 => "SetBackgroundColor", 11 => "DefineText", 12 => "DoAction",
        22 => "DefineShape2", 26 => "PlaceObject2", 28 => "RemoveObject2", 32 => "DefineShape3", 33 => "DefineText2",
        34 => "DefineButton2", 37 => "DefineEditText", 43 => "FrameLabel", 46 => "DefineMorphShape", 56 => "ExportAssets",
        88 => "DefineFontName", 125 => "EdenFont", 126 => "EdenBitmap", 127 => "EdenSprite", 5 => "RemoveObject",
        _ => "Tag" + code,
    };
}
