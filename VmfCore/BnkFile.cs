namespace VmfCore;

/// <summary>Eintrag einer TDU2-Bank (.bnk).</summary>
public sealed class BnkEntry
{
    public int Index;
    public string Name = "";          // inkl. Endung, z.B. "main.vmf"
    public string Extension = "";     // klein, mit Punkt
    public long Offset;
    public int Size;
    public override string ToString() => $"{Name} ({Size:N0} B)";
}

/// <summary>
/// Eden-Bank ("KNAB"): lesen und einzelne Einträge ersetzen (Format wie Client/…/IO/BnkArchive.cs).
/// Speichern: passt der neue Inhalt in den alten Platz (bis zum nächsten Eintrag), wird er dort geschrieben, sonst ans Dateiende
/// (2048-ausgerichtet) gehängt; Dateitabelle (Offset/Größe) und Dateigröße (0x18) werden angepasst, alles andere bleibt bytegleich.
/// </summary>
public sealed class BnkFile
{
    public string FilePath { get; private set; } = "";
    public readonly List<BnkEntry> Entries = new();
    byte[] _data = Array.Empty<byte>();
    int _secFiles;
    readonly Dictionary<int, byte[]> _replaced = new();

    public bool Dirty => _replaced.Count > 0;

    public static BnkFile Open(string path)
    {
        var b = new BnkFile { FilePath = path, _data = File.ReadAllBytes(path) };
        b.ReadHeader();
        return b;
    }

    public byte[] Read(BnkEntry e)
    {
        if (_replaced.TryGetValue(e.Index, out var r)) return (byte[])r.Clone();
        var o = new byte[e.Size];
        Buffer.BlockCopy(_data, (int)e.Offset, o, 0, e.Size);
        return o;
    }

    public BnkEntry Find(string name) => Entries.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Replace(BnkEntry e, byte[] data) => _replaced[e.Index] = data;

    /// <summary>Bank mit den ersetzten Einträgen speichern (path = null: in die geöffnete Datei).</summary>
    public void Save(string path = null)
    {
        path ??= FilePath;
        var sorted = Entries.OrderBy(x => x.Offset).ToList();
        var buf = (byte[])_data.Clone();
        var appended = new List<(BnkEntry e, byte[] d)>();
        foreach (var kv in _replaced)
        {
            var e = Entries[kv.Key];
            int idx = sorted.IndexOf(e);
            long next = idx + 1 < sorted.Count ? sorted[idx + 1].Offset : _data.Length;
            if (kv.Value.Length <= next - e.Offset)
            {
                // passt in den alten Platz: dort schreiben, Rest bis zum alten Ende nullen
                Buffer.BlockCopy(kv.Value, 0, buf, (int)e.Offset, kv.Value.Length);
                for (long i = e.Offset + kv.Value.Length; i < e.Offset + e.Size && i < next; i++) buf[i] = 0;
                SetEntry(buf, e.Index, e.Offset, kv.Value.Length);
            }
            else appended.Add((e, kv.Value));
        }
        // zu große Einträge ans Ende (2048-ausgerichtet wie die Originalbänke)
        long len = buf.Length;
        var placed = new List<(long pos, byte[] d, BnkEntry e)>();
        foreach (var (e, d) in appended)
        {
            long pos = (len + 2047) / 2048 * 2048;
            placed.Add((pos, d, e));
            len = pos + d.Length;
        }
        var all = new byte[len];
        Buffer.BlockCopy(buf, 0, all, 0, buf.Length);
        foreach (var (pos, d, e) in placed)
        {
            Buffer.BlockCopy(d, 0, all, (int)pos, d.Length);
            SetEntry(all, e.Index, pos, d.Length);
        }
        Bin.W32(all, 0x18, (uint)all.Length);
        string tmpPath = path + ".tmp";
        File.WriteAllBytes(tmpPath, all);
        File.Move(tmpPath, path, true);
        _data = all;
        _replaced.Clear();
        FilePath = path;
        Entries.Clear();
        ReadHeader();
    }

    void SetEntry(byte[] d, int index, long offset, int size)
    {
        int o = _secFiles + 8 + index * 20;
        Bin.W32(d, o, (uint)offset);
        Bin.W32(d, o + 4, (uint)size);
    }

    void ReadHeader()
    {
        var head = _data;
        if (!Bin.TagIs(head, 8, "KNAB")) throw new InvalidDataException("Not a TDU2 bank (KNAB missing): " + FilePath);
        int nFiles = (int)Bin.U32(head, 0x28);
        _secFiles = (int)Bin.U32(head, 0x30);
        int secNames = (int)Bin.U32(head, 0x38);
        int secOrder = (int)Bin.U32(head, 0x3C);
        for (int i = 0; i < nFiles; i++)
        {
            int o = _secFiles + 8 + i * 20;
            Entries.Add(new BnkEntry { Index = i, Offset = Bin.U32(head, o), Size = (int)Bin.U32(head, o + 4) });
        }
        var names = new List<string[]>();
        int namesSize = (int)Bin.U32(head, secNames);
        int p = secNames + 8, end = Math.Min(secNames + 8 + namesSize, secOrder);
        var stack = new List<string>();
        bool v2 = p < end && head[p] != 0 && head[p] < 0x80;
        while (p < end && head[p] != 0) p = v2 ? WalkNameV2(head, p, end, stack, names) : WalkName(head, p, stack, names);
        int orderSize = (int)Bin.U32(head, secOrder);
        bool wide = nFiles > 0 && orderSize >= nFiles * 2;
        for (int ni = 0; ni < names.Count && ni < nFiles; ni++)
        {
            int fi = wide ? Bin.U16(head, secOrder + 8 + ni * 2) : head[secOrder + 8 + ni];
            if (fi < 0 || fi >= nFiles) continue;
            var path = names[ni];
            string leaf = path[^1], ext = path.Length >= 2 ? path[^2] : "";
            Entries[fi].Name = leaf + ext;
            Entries[fi].Extension = ext.ToLowerInvariant();
        }
        foreach (var e in Entries) if (string.IsNullOrEmpty(e.Name)) e.Name = "file" + e.Index;
    }

    static int WalkName(byte[] d, int p, List<string> stack, List<string[]> outNames)
    {
        byte b = d[p];
        if (b >= 0x80)
        {
            int len = 0x100 - b, q = p + 1, count = 0, shift = 0;
            while (true) { byte v = d[q++]; count |= (v & 0x7F) << shift; if ((v & 0x80) == 0) break; shift += 7; }
            string s = Bin.Latin1(d, q, len);
            p = q + len;
            stack.Add(s);
            for (int i = 0; i < count; i++) p = WalkName(d, p, stack, outNames);
            stack.RemoveAt(stack.Count - 1);
            return p;
        }
        string leaf = Bin.Latin1(d, p + 1, b);
        var full = new string[stack.Count + 1]; stack.CopyTo(full); full[^1] = leaf;
        outNames.Add(full);
        return p + 1 + b;
    }

    static int WalkNameV2(byte[] d, int p, int end, List<string> stack, List<string[]> outNames)
    {
        bool leaf = stack.Count > 0 && stack[^1].StartsWith(".");
        int len = d[p];
        if (leaf)
        {
            var full = new string[stack.Count + 1]; stack.CopyTo(full); full[^1] = Bin.Latin1(d, p + 1, len);
            outNames.Add(full);
            return p + 1 + len;
        }
        int q = p + 1, count = 0, shift = 0;
        while (q < end) { byte v = d[q++]; count |= (v & 0x7F) << shift; if ((v & 0x80) == 0) break; shift += 7; }
        stack.Add(Bin.Latin1(d, q, len));
        p = q + len;
        for (int i = 0; i < count && p < end; i++) p = WalkNameV2(d, p, end, stack, outNames);
        stack.RemoveAt(stack.Count - 1);
        return p;
    }
}
