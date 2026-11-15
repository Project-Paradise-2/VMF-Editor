using System.Globalization;
using System.Text;

namespace VmfCore;

/// <summary>Textsatz eines DefineEditText mit Textur-Font wie im Spiel-Nachbau (Client/…/Flash/VmfText.cs).</summary>
public static class TextLayout
{
    public struct Quad { public RectF Dest; public VmfModel.Glyph Glyph; public Rgba? Color; }

    public static float Ascent(VmfModel.Font f)
    {
        if (f.Ascent >= 500 && f.Ascent <= 1300) return f.Ascent;
        foreach (char c in "AHEIT") if (f.Glyphs.TryGetValue(c, out var g) && g.Top > 200 && g.Top < 1200) return g.Top * 1.27f;
        return 920f;
    }

    public static float Advance(VmfModel.Font f, char c, float size)
    {
        if (f.Advances.TryGetValue(c, out var adv) && adv > 0 && adv < 4096) return adv * size / 1024f;
        if (f.Glyphs.TryGetValue(c, out var g) && g.W > 0 && g.W < 4096) return (g.W - 2 * g.XOff) * size / 1024f;
        return size * 0.35f;
    }

    public static float LineHeight(VmfModel.EditText t, VmfModel.Font font, float size)
    {
        float asc = Ascent(font);
        return size * (asc + MathF.Max(200f, 1024f - asc * 0.85f)) / 1024f * 0.92f + t.Leading;
    }

    struct Run { public string Text; public Rgba? Color; }

    static List<Run> Markup(string s, bool html)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(s)) return runs;
        s = s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n").Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(); Rgba? color = null; var stack = new Stack<Rgba?>();
        void Flush() { if (sb.Length > 0) { runs.Add(new Run { Text = sb.ToString(), Color = color }); sb.Clear(); } }
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '#')
            {
                int end = s.IndexOf('#', i + 1);
                if (end > i)
                {
                    string tag = s.Substring(i + 1, end - i - 1);
                    if (tag.StartsWith("color:", StringComparison.OrdinalIgnoreCase) && Hex(tag[6..], out var col)) { Flush(); color = col; i = end + 1; continue; }
                    if (string.Equals(tag, "nocolor", StringComparison.OrdinalIgnoreCase)) { Flush(); color = null; i = end + 1; continue; }
                    if (tag.StartsWith("command:", StringComparison.OrdinalIgnoreCase)) { sb.Append('[').Append(tag[8..]).Append(']'); i = end + 1; continue; }
                }
            }
            else if (html && c == '<')
            {
                int end = s.IndexOf('>', i + 1);
                if (end > i)
                {
                    string tag = s.Substring(i + 1, end - i - 1).Trim(), low = tag.ToLowerInvariant();
                    if (low is "br" or "br/" or "br /" or "/p") sb.Append('\n');
                    else if (low.StartsWith("font"))
                    {
                        Flush(); stack.Push(color);
                        int h = tag.IndexOf('#');
                        if (h >= 0 && h + 7 <= tag.Length && Hex(tag.Substring(h + 1, 6), out var col)) color = col;
                    }
                    else if (low == "/font") { Flush(); color = stack.Count > 0 ? stack.Pop() : null; }
                    i = end + 1; continue;
                }
            }
            sb.Append(c); i++;
        }
        Flush();
        return runs;
    }

    static bool Hex(string h, out Rgba c)
    {
        c = default; h = h.Trim().TrimStart('#');
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if ((h.Length != 6 && h.Length != 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        c = h.Length == 6 ? new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255) : new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)Math.Max(1, (int)(v & 255)));
        if (h.Length == 8 && (v & 255) == 0) c.A = 255;
        return true;
    }

    /// <summary>Glyphen-Rechtecke (Koordinaten des Textfelds) für den Text s.</summary>
    public static List<Quad> Layout(VmfModel.EditText t, VmfModel.Font font, string s)
    {
        var quads = new List<Quad>();
        if (font == null || string.IsNullOrEmpty(s)) return quads;
        float size = t.Height > 0 ? t.Height : 12f;
        var chars = new List<(char c, Rgba? col)>();
        foreach (var r in Markup(s, t.Html)) foreach (var c in r.Text) chars.Add((t.Password ? '*' : c, r.Color));
        float avail = t.Bounds.W - 4f - t.LeftMargin - t.RightMargin;
        bool wrap = t.WordWrap && avail > size;
        var laid = new List<(char c, float x, int line, Rgba? col)>();
        var lineW = new List<float>();
        int line = 0, lineStart = 0, lastSpace = -1; float x = t.Indent;
        void EndLine()
        {
            float w = 0f;
            for (int k = lineStart; k < laid.Count; k++) if (laid[k].c != ' ') w = MathF.Max(w, laid[k].x + Advance(font, laid[k].c, size));
            lineW.Add(w);
        }
        foreach (var (c, col) in chars)
        {
            if (c == '\n') { EndLine(); line++; x = 0f; lineStart = laid.Count; lastSpace = -1; continue; }
            float adv = Advance(font, c, size);
            if (wrap && c != ' ' && x + adv > avail && laid.Count > lineStart)
            {
                if (lastSpace >= lineStart)
                {
                    var moved = laid.GetRange(lastSpace + 1, laid.Count - lastSpace - 1);
                    laid.RemoveRange(lastSpace, laid.Count - lastSpace);
                    EndLine(); line++; lineStart = laid.Count; x = 0f;
                    foreach (var m in moved) { laid.Add((m.c, x, line, m.col)); x += Advance(font, m.c, size); }
                }
                else { EndLine(); line++; lineStart = laid.Count; x = 0f; }
                lastSpace = -1;
                if (c == ' ') continue;
            }
            if (c == ' ') lastSpace = laid.Count;
            laid.Add((c, x, line, col)); x += adv;
        }
        EndLine();
        float lh = LineHeight(t, font, size), k2 = size / 1024f;
        float baseline0 = t.Bounds.Y + 2f + size * Ascent(font) / 1024f;
        float left = t.Bounds.X + 2f + t.LeftMargin, right = t.Bounds.XMax - 2f - t.RightMargin;
        foreach (var lc in laid)
        {
            if (!font.Glyphs.TryGetValue(lc.c, out var g) || g.H <= 0 || g.V1 <= g.V0) continue;
            float lw = lc.line < lineW.Count ? lineW[lc.line] : 0f;
            float x0 = t.Align == 1 ? right - lw : t.Align == 2 ? (left + right - lw) * 0.5f : left;
            float baseline = baseline0 + lc.line * lh;
            quads.Add(new Quad { Dest = new RectF(x0 + lc.x - g.XOff * k2, baseline - g.Top * k2, g.W * k2, g.H * k2), Glyph = g, Color = lc.col });
        }
        return quads;
    }
}
