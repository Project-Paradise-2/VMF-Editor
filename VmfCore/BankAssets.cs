namespace VmfCore;

/// <summary>
/// Inhalt einer geöffneten Bank: der Film (.vmf) und seine Texturen (.2db). Atlas-Seiten wie im Spiel-Nachbau über die
/// Bitmapgröße bestimmt (Kopftabelle ist unzuverlässig), Font-Glyphen aus fla_&lt;Font-ID&gt; (scharfe Variante).
/// </summary>
public sealed class BankAssets
{
    public BnkFile Bank { get; }
    public BnkEntry VmfEntry { get; private set; }
    public VmfFile File { get; private set; }
    public VmfModel Model { get; private set; }
    readonly Dictionary<string, BnkEntry> _tex = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PixelImage> _img = new(StringComparer.OrdinalIgnoreCase);

    public BankAssets(BnkFile bank, BnkEntry vmf)
    {
        Bank = bank; VmfEntry = vmf;
        foreach (var e in bank.Entries)
            if (e.Extension == ".2db") _tex[Path.GetFileNameWithoutExtension(e.Name)] = e;
        File = VmfFile.Parse(bank.Read(vmf));
        Rebuild();
    }

    /// <summary>Modell nach einer Änderung neu aufbauen (die Tags in File sind maßgeblich).</summary>
    public void Rebuild()
    {
        Model = VmfModel.Build(File, n => _tex.ContainsKey(n));
        AssignPagesBySize();
    }

    public IEnumerable<string> TextureNames => _tex.Keys;

    public PixelImage Image(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_img)
        {
            if (_img.TryGetValue(name, out var i)) return i;
            i = _tex.TryGetValue(name, out var e) ? PixelImage.Decode2db(Bank.Read(e)) : null;
            _img[name] = i;
            return i;
        }
    }

    public (PixelImage img, VmfModel.Bitmap bmp) Bitmap(int id)
    {
        if (!Model.Chars.TryGetValue(id, out var c) || c is not VmfModel.Bitmap b) return (null, null);
        return (Image(b.TextureName), b);
    }

    public PixelImage FontImage(VmfModel.Font f) => Image($"fla_{f.Id:0000}") ?? Image(f.TextureName);

    void AssignPagesBySize()
    {
        var pages = new List<(string name, int w, int h)>();
        foreach (var kv in _tex)
            if (kv.Key.StartsWith("page_", StringComparison.OrdinalIgnoreCase))
            {
                var head = Bank.Read(kv.Value);
                if (head.Length > 0x2C) pages.Add((kv.Key, Bin.U16(head, 0x28), Bin.U16(head, 0x2A)));
            }
        if (pages.Count == 0) return;
        foreach (var c in Model.Chars.Values)
        {
            if (c is not VmfModel.Bitmap b || !b.TextureName.StartsWith("page_", StringComparison.OrdinalIgnoreCase)) continue;
            if (pages.Count == 1) { b.TextureName = pages[0].name; continue; }
            float du = MathF.Abs(b.U1 - b.U0), dv = MathF.Abs(b.V1 - b.V0);
            if (du < 1e-6f || dv < 1e-6f) continue;
            float ew = b.Width / du, eh = b.Height / dv, best = float.MaxValue;
            foreach (var p in pages)
            {
                float err = MathF.Abs(MathF.Log(ew / Math.Max(1, p.w))) + MathF.Abs(MathF.Log(eh / Math.Max(1, p.h)));
                if (err < best) { best = err; b.TextureName = p.name; }
            }
        }
    }

    /// <summary>Film in die Bank schreiben und speichern; beim ersten Speichern Sicherung "&lt;bank&gt;.orig".</summary>
    public void Save()
    {
        string orig = Bank.FilePath + ".orig";
        if (!System.IO.File.Exists(orig)) System.IO.File.Copy(Bank.FilePath, orig);
        Bank.Replace(VmfEntry, File.Write());
        Bank.Save();
        // Tabelle wurde neu gelesen: Einträge neu zuordnen
        VmfEntry = Bank.Entries[VmfEntry.Index];
        _tex.Clear();
        foreach (var e in Bank.Entries) if (e.Extension == ".2db") _tex[Path.GetFileNameWithoutExtension(e.Name)] = e;
    }
}
