using System.Text;

namespace VmfCore;

/// <summary>
/// Inhalt eines VMF-Films für Anzeige und Bearbeitung: Charaktere (Shapes, Bitmaps, Fonts, Textfelder, Buttons, Sprites) und
/// Zeitleisten. Jedes Objekt kennt seinen Tag (VmfTag) – Änderungen kodieren genau diesen Tag neu (siehe VmfEdit).
/// Gelesen wie im Spiel-Nachbau (Client/…/Flash/VmfMovie.cs).
/// </summary>
public sealed class VmfModel
{
    public abstract class Character { public int Id; public VmfTag Tag; public abstract string Kind { get; } }

    public sealed class FillStyle
    {
        public int Type;                 // 0 solid, 0x10 linear, 0x12 radial, 0x13 focal, 0x40..0x43 bitmap
        public Rgba Color;
        public Matrix2D Matrix;
        public byte[] Ratios = Array.Empty<byte>(); public Rgba[] Colors = Array.Empty<Rgba>();
        public int BitmapId;
    }
    public sealed class LineStyle { public float Width; public Rgba Color; }
    public sealed class Path { public int Fill0, Fill1, Line; public List<Vec2> Points = new(); }

    public sealed class Shape : Character
    {
        public RectF Bounds; public bool Morph;
        public List<FillStyle> Fills = new(); public List<LineStyle> Lines = new(); public List<Path> Paths = new();
        public override string Kind => Morph ? "MorphShape" : "Shape";
    }
    public sealed class Bitmap : Character
    {
        public int Width, Height; public float U0, V0, U1, V1; public int Page = -1; public string TextureName = "";
        public override string Kind => "Bitmap";
    }
    public struct Glyph { public float U0, U1, V0, V1; public short W, H, XOff, Top; }
    public sealed class Font : Character
    {
        public string Name = "", TextureName = "";
        public int Ascent = 1024, Descent, Leading;
        public readonly Dictionary<char, Glyph> Glyphs = new();
        public readonly Dictionary<char, short> Advances = new();
        public override string Kind => "Font";
    }
    public sealed class EditText : Character
    {
        public RectF Bounds; public string Variable = "", Text = ""; public float Height; public Rgba Color = new(255, 255, 255, 255);
        public int Align, FontId; public bool Multiline, WordWrap, Html, Password, KeyText;
        public float LeftMargin, RightMargin, Indent, Leading;
        public override string Kind => "EditText";
    }
    public sealed class ButtonRecord { public int States, CharId, Depth; public Matrix2D Matrix = Matrix2D.Identity; public CxForm Cx = CxForm.Identity; }
    public sealed class ButtonAction { public int Conditions; public byte[] Code = Array.Empty<byte>(); }
    public sealed class Button : Character
    {
        public readonly List<ButtonRecord> Records = new();
        public readonly List<ButtonAction> Actions = new();
        public override string Kind => "Button";
    }

    public enum OpKind { Place, Remove, Action }
    public sealed class FrameOp
    {
        public OpKind Kind; public int Depth; public VmfTag Tag;
        public bool Move, HasChar, HasMatrix, HasCx, HasRatio, HasName, HasClip;
        public int CharId, Ratio, ClipDepth; public Matrix2D Matrix = Matrix2D.Identity; public CxForm Cx = CxForm.Identity; public string Name;
        public byte[] ClipActionsRaw;   // PlaceObject2 mit Clip-Aktionen (Flag 0x80): unverändert mitgeschrieben
        public byte[] Actions;
    }
    public sealed class Label { public string Name = ""; public int Frame; public VmfTag Tag; }

    public sealed class Timeline
    {
        public readonly List<List<FrameOp>> Frames = new();
        public readonly List<Label> Labels = new();
        public List<VmfTag> TagList;    // Tag-Liste, aus der die Zeitleiste stammt (Hauptfilm oder Sprite-Kinder)
        public int FrameCount => Frames.Count;
        public int LabelFrame(string name) => Labels.Find(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))?.Frame ?? -1;
    }
    public sealed class Sprite : Character
    {
        public Timeline Timeline = new();
        public override string Kind => "Sprite";
    }

    public readonly Dictionary<int, Character> Chars = new();
    public readonly Dictionary<int, Font> Fonts = new();
    public readonly Dictionary<string, int> Exports = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<int, int> PageOf = new();
    public Timeline Root = new();
    public float FrameRate = 30f;
    public int Errors;
    public VmfFile File;

    public static VmfModel Build(VmfFile f, Func<string, bool> textureExists = null)
    {
        var m = new VmfModel { File = f, FrameRate = f.FrameRate };
        var h = f.Header;
        if (h.Length > 21 && h[18] != 0)
        {
            int n = Bin.U16(h, 19);
            for (int i = 0, o = 21; i < n && o + 4 <= h.Length; i++, o += 4) m.PageOf[Bin.U16(h, o + 2)] = Bin.U16(h, o);
        }
        m.Root = m.ReadTimeline(f.Tags);
        foreach (var c in m.Chars.Values)
            if (c is Bitmap b)
            {
                string own = $"fla_{b.Id:0000}";
                if (textureExists != null && textureExists(own)) b.TextureName = own;
                else if (m.PageOf.TryGetValue(b.Id, out var pg)) { b.Page = pg; b.TextureName = $"page_{pg + 1:000}"; }
                else b.TextureName = own;
            }
        return m;
    }

    Timeline ReadTimeline(List<VmfTag> tags)
    {
        var tl = new Timeline { TagList = tags };
        var cur = new List<FrameOp>();
        foreach (var t in tags)
        {
            var d = t.Body;
            try
            {
                switch (t.Code)
                {
                    case 0: goto done;
                    case 1: tl.Frames.Add(cur); cur = new List<FrameOp>(); break;
                    case 43: { int q = 0; tl.Labels.Add(new Label { Name = Bin.CString(d, ref q, d.Length), Frame = tl.Frames.Count, Tag = t }); break; }
                    case 26: { var op = ReadPlace2(d); op.Tag = t; cur.Add(op); break; }
                    case 28: cur.Add(new FrameOp { Kind = OpKind.Remove, Depth = Bin.U16(d, 0), Tag = t }); break;
                    case 5: cur.Add(new FrameOp { Kind = OpKind.Remove, Depth = Bin.U16(d, 2), Tag = t }); break;
                    case 12: cur.Add(new FrameOp { Kind = OpKind.Action, Actions = d, Tag = t }); break;
                    case 2: case 22: case 32: ReadShape(t, t.Code == 2 ? 1 : t.Code == 22 ? 2 : 3); break;
                    case 46: ReadMorph(t); break;
                    case 126:
                        Chars[Bin.U16(d, 0)] = new Bitmap
                        {
                            Id = Bin.U16(d, 0), Tag = t, Width = Bin.U16(d, 2), Height = Bin.U16(d, 4),
                            U0 = Bin.F32(d, 6), V0 = Bin.F32(d, 10), U1 = Bin.F32(d, 14), V1 = Bin.F32(d, 18),
                        };
                        break;
                    case 127:
                        if (t.IsSprite) { var sp = new Sprite { Id = t.SpriteId, Tag = t }; sp.Timeline = ReadTimeline(t.Children); Chars[sp.Id] = sp; }
                        break;
                    case 37: ReadEditText(t); break;
                    case 125: ReadFont(t); break;
                    case 34: ReadButton2(t); break;
                    case 56:
                        {
                            int n = Bin.U16(d, 0), q = 2;
                            for (int i = 0; i < n && q + 2 < d.Length; i++)
                            {
                                int cid = Bin.U16(d, q); q += 2;
                                string name = Bin.CString(d, ref q, d.Length);
                                if (!string.IsNullOrEmpty(name)) Exports[name] = cid;
                            }
                            break;
                        }
                }
            }
            catch (Exception) { Errors++; }
        }
    done:
        if (cur.Count > 0 || tl.Frames.Count == 0) tl.Frames.Add(cur);
        return tl;
    }

    public static FrameOp ReadPlace2(byte[] d)
    {
        var br = new BitReader(d, 0);
        int f = br.U8();
        var op = new FrameOp { Kind = OpKind.Place, Depth = br.U16(), Move = (f & 1) != 0 };
        if ((f & 2) != 0) { op.HasChar = true; op.CharId = br.U16(); }
        if ((f & 4) != 0) { op.HasMatrix = true; op.Matrix = br.Matrix(); }
        if ((f & 8) != 0) { op.HasCx = true; op.Cx = br.CxForm(true); }
        if ((f & 16) != 0) { op.HasRatio = true; op.Ratio = br.U16(); }
        if ((f & 32) != 0) { op.HasName = true; int q = br.Pos; op.Name = Bin.CString(d, ref q, d.Length); br.Pos = q; }
        if ((f & 64) != 0) { op.HasClip = true; op.ClipDepth = br.U16(); }
        if ((f & 128) != 0) { int q = br.Pos; op.ClipActionsRaw = d[q..]; }
        return op;
    }

    // ---------------------------------------------------------------- Shapes
    static List<FillStyle> ReadFills(BitReader br, int ver, bool morph)
    {
        int n = br.U8(); if (n == 0xFF && ver >= 2) n = br.U16();
        var list = new List<FillStyle>(n);
        for (int i = 0; i < n; i++)
        {
            var fs = new FillStyle { Type = br.U8() };
            if (fs.Type == 0) { fs.Color = br.Rgb(ver >= 3 || morph); if (morph) br.Rgb(true); }
            else if (fs.Type == 0x10 || fs.Type == 0x12 || fs.Type == 0x13)
            {
                fs.Matrix = br.Matrix(); if (morph) br.Matrix();
                int g = br.U8(), cnt = g & 15;
                fs.Ratios = new byte[cnt]; fs.Colors = new Rgba[cnt];
                for (int k = 0; k < cnt; k++) { fs.Ratios[k] = (byte)br.U8(); fs.Colors[k] = br.Rgb(ver >= 3 || morph); if (morph) { br.U8(); br.Rgb(true); } }
                if (fs.Type == 0x13) br.U16();
            }
            else if (fs.Type >= 0x40 && fs.Type <= 0x43) { fs.BitmapId = br.U16(); fs.Matrix = br.Matrix(); if (morph) br.Matrix(); }
            else throw new FormatException("fill style " + fs.Type);
            list.Add(fs);
        }
        return list;
    }

    static List<LineStyle> ReadLines(BitReader br, int ver, bool morph)
    {
        int n = br.U8(); if (n == 0xFF) n = br.U16();
        var list = new List<LineStyle>(n);
        for (int i = 0; i < n; i++)
        {
            var ls = new LineStyle { Width = br.U16() / 20f };
            if (morph) br.U16();
            ls.Color = br.Rgb(ver >= 3 || morph);
            if (morph) br.Rgb(true);
            list.Add(ls);
        }
        return list;
    }

    void ReadShape(VmfTag t, int ver)
    {
        var d = t.Body;
        var br = new BitReader(d, 0);
        var s = new Shape { Id = br.U16(), Tag = t, Bounds = br.Rect() };
        s.Fills = ReadFills(br, ver, false);
        s.Lines = ReadLines(br, ver, false);
        int q = br.Pos, end = d.Length;
        int n = Bin.U16(d, q); q += 2;
        for (int i = 0; i < n && q + 15 <= end; i++)
        {
            var path = new Path { Line = d[q], Fill0 = Bin.U16(d, q + 1), Fill1 = Bin.U16(d, q + 3) };
            int x = Bin.S32(d, q + 5), y = Bin.S32(d, q + 9), ne = Bin.U16(d, q + 13);
            q += 15;
            path.Points.Add(new Vec2(x / 20f, y / 20f));
            for (int k = 0; k < ne && q + 16 <= end; k++, q += 16)
            {
                int cx = Bin.S32(d, q), cy = Bin.S32(d, q + 4), ax = Bin.S32(d, q + 8), ay = Bin.S32(d, q + 12);
                AddEdge(path.Points, x, y, cx, cy, ax, ay);
                x = ax; y = ay;
            }
            q++;
            s.Paths.Add(path);
        }
        Chars[s.Id] = s;
    }

    static void AddEdge(List<Vec2> pts, int x, int y, int cx, int cy, int ax, int ay)
    {
        if (cx == ax && cy == ay) { pts.Add(new Vec2(ax / 20f, ay / 20f)); return; }
        int steps = Math.Clamp((Math.Abs(ax - x) + Math.Abs(ay - y)) / 40, 2, 16);
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps, u = 1f - t;
            pts.Add(new Vec2((u * u * x + 2 * u * t * cx + t * t * ax) / 20f, (u * u * y + 2 * u * t * cy + t * t * ay) / 20f));
        }
    }

    void ReadMorph(VmfTag t)
    {
        var d = t.Body;
        var br = new BitReader(d, 0);
        var s = new Shape { Id = br.U16(), Tag = t, Bounds = br.Rect(), Morph = true };
        br.Rect(); br.Pos += 4;
        s.Fills = ReadFills(br, 3, true);
        s.Lines = ReadLines(br, 3, true);
        int nf = br.UB(4), nl = br.UB(4);
        float x = 0, y = 0; int f0 = 0, f1 = 0, ln = 0;
        Path cur = null;
        void Start() { cur = new Path { Fill0 = f0, Fill1 = f1, Line = ln }; cur.Points.Add(new Vec2(x, y)); s.Paths.Add(cur); }
        Start();
        int guard = 0;
        while (br.Pos < d.Length && guard++ < 100000)
        {
            if (br.UB(1) == 0)
            {
                int flags = br.UB(5);
                if (flags == 0) break;
                if ((flags & 1) != 0) { int nb = br.UB(5); x = br.SB(nb) / 20f; y = br.SB(nb) / 20f; }
                if ((flags & 2) != 0) f0 = br.UB(nf);
                if ((flags & 4) != 0) f1 = br.UB(nf);
                if ((flags & 8) != 0) ln = br.UB(nl);
                Start();
            }
            else if (br.UB(1) == 1)
            {
                int nb = br.UB(4) + 2; float dx = 0, dy = 0;
                if (br.UB(1) == 1) { dx = br.SB(nb) / 20f; dy = br.SB(nb) / 20f; }
                else if (br.UB(1) == 1) dy = br.SB(nb) / 20f; else dx = br.SB(nb) / 20f;
                x += dx; y += dy; cur.Points.Add(new Vec2(x, y));
            }
            else
            {
                int nb = br.UB(4) + 2;
                float cx = x + br.SB(nb) / 20f, cy = y + br.SB(nb) / 20f, ax = cx + br.SB(nb) / 20f, ay = cy + br.SB(nb) / 20f;
                for (int i = 1; i <= 8; i++) { float tt = i / 8f, u = 1f - tt; cur.Points.Add(new Vec2(u * u * x + 2 * u * tt * cx + tt * tt * ax, u * u * y + 2 * u * tt * cy + tt * tt * ay)); }
                x = ax; y = ay;
            }
        }
        Chars[s.Id] = s;
    }

    void ReadFont(VmfTag t)
    {
        var d = t.Body; int end = d.Length;
        var f = new Font { Id = Bin.U16(d, 0), Tag = t };
        int tex = Bin.U16(d, 2);
        int q = 5;
        f.Name = Bin.CString(d, ref q, end);
        f.TextureName = $"fla_{(tex != 0 ? tex : f.Id):0000}";
        if (q + 4 > end) { Fonts[f.Id] = f; return; }
        int n = Bin.U16(d, q), nTex = Bin.U16(d, q + 2); q += 4;
        for (int i = 0; i < nTex && q + 26 <= end; i++, q += 26)
            f.Glyphs[(char)Bin.U16(d, q)] = new Glyph
            {
                U0 = Bin.F32(d, q + 2), U1 = Bin.F32(d, q + 6), V0 = Bin.F32(d, q + 10), V1 = Bin.F32(d, q + 14),
                W = Bin.S16(d, q + 18), H = Bin.S16(d, q + 20), XOff = Bin.S16(d, q + 22), Top = Bin.S16(d, q + 24),
            };
        if (tex != 0) q += 26 * nTex;
        int codes = q + 2 * n, adv = codes + 2 * n + 6;
        if (adv + 2 * n <= end)
        {
            f.Ascent = Bin.U16(d, codes + 2 * n); f.Descent = Bin.S16(d, codes + 2 * n + 2); f.Leading = Bin.S16(d, codes + 2 * n + 4);
            for (int i = 0; i < n; i++) f.Advances[(char)Bin.U16(d, codes + 2 * i)] = Bin.S16(d, adv + 2 * i);
        }
        Fonts[f.Id] = f;
    }

    static bool LooksLikeVarRef(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
        foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '/')) return false;
        if (s.StartsWith(":") || s.StartsWith("/:") || s.StartsWith("./") || s.StartsWith("../")) return s.IndexOf(':') >= 0 && s.Length > 2;
        return s.StartsWith("_root.") || s.StartsWith("root.") || s.StartsWith("_parent.") || s.StartsWith("parent.");
    }

    void ReadEditText(VmfTag t)
    {
        var d = t.Body;
        var et = EditTextLayout.Read(d);
        var e = new EditText
        {
            Id = et.Id, Tag = t, Bounds = et.Bounds, FontId = et.FontId, Height = et.Height, Color = et.Color, Align = et.Align,
            LeftMargin = et.LeftMargin, RightMargin = et.RightMargin, Indent = et.Indent, Leading = et.Leading,
            WordWrap = (et.F1 & 0x40) != 0, Multiline = (et.F1 & 0x20) != 0, Password = (et.F1 & 0x10) != 0, Html = (et.F2 & 0x02) != 0,
            Variable = et.Variable ?? "",
        };
        if (et.HasText)
        {
            string t0 = et.Text ?? "";
            string raw = t0;
            if (t0.StartsWith("_")) { t0 = t0[1..]; e.KeyText = true; }
            bool printable = t0.Length > 0;
            foreach (var ch in t0) if (ch < 0x20 && ch != '\n' && ch != '\r') { printable = false; break; }
            e.Text = printable ? t0 : "";
            if (string.IsNullOrEmpty(e.Variable) && printable && LooksLikeVarRef(raw))
            {
                string v = raw;
                if (v.StartsWith("root.") || v.StartsWith("parent.")) v = "_" + v;
                e.Variable = v; e.Text = ""; e.KeyText = false;
            }
        }
        Chars[e.Id] = e;
    }

    void ReadButton2(VmfTag t)
    {
        var d = t.Body; int end = d.Length;
        var b = new Button { Id = Bin.U16(d, 0), Tag = t };
        int q = 3;
        int actOff = Bin.U16(d, q);
        int actStart = actOff != 0 ? q + actOff : end;
        q += 2;
        while (q < end && q < actStart && d[q] != 0)
        {
            var br = new BitReader(d, q);
            int fl = br.U8();
            var r = new ButtonRecord { States = fl & 0x0F, CharId = br.U16(), Depth = br.U16() };
            r.Matrix = br.Matrix();
            r.Cx = br.CxForm(true);
            if ((fl & 0x10) != 0) break;
            if ((fl & 0x20) != 0) br.U8();
            q = br.Pos;
            b.Records.Add(r);
        }
        q = actStart;
        while (actOff != 0 && q + 4 <= end)
        {
            int size = Bin.U16(d, q), cond = Bin.U16(d, q + 2);
            int s0 = q + 4, e = size == 0 ? end : Math.Min(end, q + size);
            if (e > s0) b.Actions.Add(new ButtonAction { Conditions = cond, Code = d[s0..e] });
            if (size == 0) break;
            q += size;
        }
        Chars[b.Id] = b;
    }
}

/// <summary>
/// DefineEditText (Tag 37) in seinen Teilen – zum Lesen und für das bytegenaue Neuschreiben einzelner Felder.
/// Eden: nach dem Variablennamen u32 (Flags/Hash), danach der Anfangstext (UTF-8). Nicht verstandene Teile bleiben roh erhalten.
/// </summary>
public sealed class EditTextLayout
{
    public int Id; public RectF Bounds; public int F1, F2;
    public int FontId; public float Height; public Rgba Color = new(255, 255, 255, 255); public int MaxLen;
    public int Align; public float LeftMargin, RightMargin, Indent, Leading;
    public string Variable = ""; public bool HasText; public uint TextFlags; public string Text = "";
    public byte[] Rest = Array.Empty<byte>();
    byte[] _layoutRaw;   // 9 Byte Layout unverändert

    public static EditTextLayout Read(byte[] d)
    {
        var br = new BitReader(d, 0);
        var e = new EditTextLayout { Id = br.U16(), Bounds = br.Rect() };
        e.F1 = br.U8(); e.F2 = br.U8();
        int p = br.Pos;
        if ((e.F1 & 0x01) != 0) { e.FontId = Bin.U16(d, p); e.Height = Bin.U16(d, p + 2) / 20f; p += 4; }
        if ((e.F1 & 0x04) != 0) { e.Color = new Rgba(d[p], d[p + 1], d[p + 2], d[p + 3]); p += 4; }
        if ((e.F1 & 0x02) != 0) { e.MaxLen = Bin.U16(d, p); p += 2; }
        if ((e.F2 & 0x20) != 0)
        {
            e._layoutRaw = d[p..(p + 9)];
            e.Align = d[p]; e.LeftMargin = Bin.U16(d, p + 1) / 20f; e.RightMargin = Bin.U16(d, p + 3) / 20f;
            e.Indent = Bin.U16(d, p + 5) / 20f; e.Leading = Bin.S16(d, p + 7) / 20f;
            p += 9;
        }
        e.Variable = Bin.CString(d, ref p, d.Length);
        if ((e.F1 & 0x80) != 0 && p + 4 <= d.Length)
        {
            e.HasText = true;
            e.TextFlags = Bin.U32(d, p); p += 4;
            int ts = p; while (p < d.Length && d[p] != 0) p++;
            e.Text = Encoding.UTF8.GetString(d, ts, p - ts);
            if (p < d.Length) p++;
        }
        e.Rest = d[Math.Min(p, d.Length)..];
        return e;
    }

    public byte[] Write()
    {
        var w = new BitWriter();
        w.U16(Id); w.Rect(Bounds);
        int f1 = F1;
        if (HasText) f1 |= 0x80; else f1 &= ~0x80;
        w.U8(f1); w.U8(F2);
        if ((f1 & 0x01) != 0) { w.U16(FontId); w.U16((int)MathF.Round(Height * 20)); }
        if ((f1 & 0x04) != 0) w.Bytes(new[] { Color.R, Color.G, Color.B, Color.A });
        if ((f1 & 0x02) != 0) w.U16(MaxLen);
        if ((F2 & 0x20) != 0)
        {
            var l = _layoutRaw != null ? (byte[])_layoutRaw.Clone() : new byte[9];
            l[0] = (byte)Align;
            Bin.W16(l, 1, (int)MathF.Round(LeftMargin * 20)); Bin.W16(l, 3, (int)MathF.Round(RightMargin * 20));
            Bin.W16(l, 5, (int)MathF.Round(Indent * 20)); Bin.W16(l, 7, (int)MathF.Round(Leading * 20));
            w.Bytes(l);
        }
        w.CString(Variable);
        if (HasText)
        {
            w.U32(TextFlags);
            w.Bytes(Encoding.UTF8.GetBytes(Text ?? "")); w.U8(0);
        }
        w.Bytes(Rest);
        return w.ToArray();
    }
}
