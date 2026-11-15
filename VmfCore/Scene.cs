namespace VmfCore;

/// <summary>
/// Anzeigeliste einer Zeitleiste in einem bestimmten Bild – ohne ActionScript, rein aus PlaceObject/RemoveObject wie die
/// Zeitleiste im Flash-Editor. Jedes Element kennt die Ops, die es angelegt bzw. zuletzt verändert haben (für die Bearbeitung).
/// Verschachtelte Sprites zeigen ihr Bild <see cref="ChildFrame"/> (Standard: das erste).
/// </summary>
public sealed class SceneItem
{
    public int Depth;
    public VmfModel.Character Char;
    public string Name;
    public Matrix2D Matrix = Matrix2D.Identity;
    public CxForm Cx = CxForm.Identity;
    public int ClipDepth, Ratio;
    public VmfModel.FrameOp PlaceOp;       // Op, das den Charakter gesetzt hat
    public VmfModel.FrameOp MatrixOp;      // Op, das die aktuelle Lage gesetzt hat (bearbeitet beim Verschieben)
    public VmfModel.FrameOp CxOp;          // Op, das die aktuelle Farbe gesetzt hat
    public int PlaceFrame, MatrixFrame;
    public List<SceneItem> Children;       // Sprite-Inhalt bzw. Button-Zustand Up
    public VmfModel.Timeline Owner;        // Zeitleiste, in der das Element steht
    public SceneItem Parent;
    public string Key;                     // Pfad ab der angezeigten Zeitleiste ("/Tiefe:Id/…"), für Ansichtseinstellungen
    public bool Hidden;                    // nur Editor-Ansicht
    public override string ToString() => $"{Depth}: {(string.IsNullOrEmpty(Name) ? "" : Name + " ")}[{Char?.Kind} {Char?.Id}]";
}

public static class Scene
{
    /// <summary>Bild, das verschachtelte Sprites zeigen (0 = erstes).</summary>
    public static Func<VmfModel.Sprite, int> ChildFrame = _ => 0;
    /// <summary>Bild für ein bestimmtes Exemplar (Ansichtseinstellung im Editor), -1 = <see cref="ChildFrame"/>.</summary>
    public static Func<SceneItem, int> FrameOf;
    /// <summary>Exemplar in der Editor-Ansicht ausblenden.</summary>
    public static Func<SceneItem, bool> HiddenOf;

    public static int FrameFor(SceneItem it, VmfModel.Sprite sp) { int f = FrameOf?.Invoke(it) ?? -1; return f >= 0 ? f : ChildFrame(sp); }

    public static List<SceneItem> Build(VmfModel m, VmfModel.Timeline tl, int frame, SceneItem parent = null, int level = 0)
    {
        var list = new SortedDictionary<int, SceneItem>();
        frame = Math.Clamp(frame, 0, Math.Max(0, tl.Frames.Count - 1));
        for (int f = 0; f <= frame; f++)
            foreach (var op in tl.Frames[f])
            {
                if (op.Kind == VmfModel.OpKind.Remove) { list.Remove(op.Depth); continue; }
                if (op.Kind != VmfModel.OpKind.Place) continue;
                list.TryGetValue(op.Depth, out var it);
                if (op.HasChar && (it == null || !op.Move || it.Char?.Id != op.CharId))
                {
                    var old = it;
                    m.Chars.TryGetValue(op.CharId, out var ch);
                    it = new SceneItem { Depth = op.Depth, Char = ch, PlaceOp = op, PlaceFrame = f, Owner = tl, Parent = parent, MatrixOp = op, MatrixFrame = f, CxOp = op };
                    if (old != null && op.Move) { it.Matrix = old.Matrix; it.Cx = old.Cx; it.Name = old.Name; it.ClipDepth = old.ClipDepth; it.MatrixOp = old.MatrixOp; it.CxOp = old.CxOp; it.MatrixFrame = old.MatrixFrame; }
                    list[op.Depth] = it;
                }
                if (it == null) continue;
                if (op.HasMatrix) { it.Matrix = op.Matrix; it.MatrixOp = op; it.MatrixFrame = f; }
                if (op.HasCx) { it.Cx = op.Cx; it.CxOp = op; }
                if (op.HasName) it.Name = op.Name;
                if (op.HasClip) it.ClipDepth = op.ClipDepth;
                if (op.HasRatio) it.Ratio = op.Ratio;
            }
        var result = list.Values.ToList();
        if (level < 24)
            foreach (var it in result)
            {
                it.Key = (parent?.Key ?? "") + "/" + it.Depth + ":" + (it.Char?.Id ?? 0);
                it.Hidden = HiddenOf?.Invoke(it) ?? false;
                if (it.Char is VmfModel.Sprite sp) it.Children = Build(m, sp.Timeline, FrameFor(it, sp), it, level + 1);
                else if (it.Char is VmfModel.Button b)
                {
                    it.Children = new List<SceneItem>();
                    foreach (var r in b.Records.Where(r => (r.States & 1) != 0).OrderBy(r => r.Depth))
                    {
                        m.Chars.TryGetValue(r.CharId, out var ch);
                        var ci = new SceneItem { Depth = r.Depth, Char = ch, Matrix = r.Matrix, Cx = r.Cx, Owner = null, Parent = it };
                        ci.Key = it.Key + "/b" + r.Depth + ":" + r.CharId;
                        ci.Hidden = HiddenOf?.Invoke(ci) ?? false;
                        if (ch is VmfModel.Sprite s2) ci.Children = Build(m, s2.Timeline, FrameFor(ci, s2), ci, level + 1);
                        it.Children.Add(ci);
                    }
                }
            }
        return result;
    }

    /// <summary>Lokale Begrenzung eines Elements (Koordinaten des Elements, ohne seine eigene Matrix).</summary>
    public static RectF LocalBounds(VmfModel m, SceneItem it)
    {
        switch (it.Char)
        {
            case VmfModel.Shape s: return s.Bounds;
            case VmfModel.EditText e: return e.Bounds;
            default:
                var r = new RectF();
                if (it.Children != null)
                    foreach (var c in it.Children) { if (c.ClipDepth > 0 || c.Hidden) continue; var cb = LocalBounds(m, c); if (!cb.IsEmpty) r = r.Union(c.Matrix.Transform(cb)); }
                return r;
        }
    }

    /// <summary>Weltmatrix (Bühne) eines Elements.</summary>
    public static Matrix2D WorldOf(SceneItem it)
    {
        var mm = it.Matrix;
        for (var p = it.Parent; p != null; p = p.Parent) mm = p.Matrix * mm;
        return mm;
    }
}
