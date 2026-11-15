using System.Globalization;
using System.Text;

namespace VmfCore;

/// <summary>
/// ActionScript-1-Bytecode (AVM1, wie in den VMF-Filmen) als Text: Disassembler und Assembler.
/// Ziel: Assemble(Disassemble(code)) ergibt dieselben Bytes.
///
/// Textformat (eine Anweisung je Zeile, „;“ leitet einen Kommentar ein):
///   ConstantPool "a" "b"              Push "text", 12, 1.5, 2.5f, true, null, undefined, r:1, @"inline"
///   GetVariable                       If L3 / Jump L3, Sprungmarke: "L3:"
///   GotoLabel "name"  GotoFrame 4  GetURL "url" "ziel"  SetTarget "pfad"  StoreRegister 1  GotoFrame2 1 [bias 5]
///   DefineFunction "name" "p1" "p2" {  … }        With {  … }
///   Unbekanntes: op 0x8F 01 02 03 (Rohbytes), Restbytes nach End: .bytes 00 11
/// Push-Zeichenketten: "x" steht im aktuellen Konstantenpool → Verweis, sonst eingebettet; @"x" erzwingt eingebettet.
/// Ganzzahlen ohne Punkt = Int32, mit Punkt/Exponent = Double, mit Endung f = Float.
/// </summary>
public static class Avm1
{
    static readonly Dictionary<int, string> Names = new()
    {
        [0x00] = "End", [0x04] = "NextFrame", [0x05] = "PrevFrame", [0x06] = "Play", [0x07] = "Stop", [0x08] = "ToggleQuality",
        [0x09] = "StopSounds", [0x0A] = "Add", [0x0B] = "Subtract", [0x0C] = "Multiply", [0x0D] = "Divide", [0x0E] = "Equals",
        [0x0F] = "Less", [0x10] = "And", [0x11] = "Or", [0x12] = "Not", [0x13] = "StringEquals", [0x14] = "StringLength",
        [0x15] = "StringExtract", [0x17] = "Pop", [0x18] = "ToInteger", [0x1C] = "GetVariable", [0x1D] = "SetVariable",
        [0x20] = "SetTarget2", [0x21] = "StringAdd", [0x22] = "GetProperty", [0x23] = "SetProperty", [0x24] = "CloneSprite",
        [0x25] = "RemoveSprite", [0x26] = "Trace", [0x27] = "StartDrag", [0x28] = "EndDrag", [0x29] = "StringLess",
        [0x2A] = "Throw", [0x2B] = "CastOp", [0x2C] = "ImplementsOp", [0x30] = "RandomNumber", [0x31] = "MBStringLength",
        [0x32] = "CharToAscii", [0x33] = "AsciiToChar", [0x34] = "GetTime", [0x35] = "MBStringExtract", [0x36] = "MBCharToAscii",
        [0x37] = "MBAsciiToChar", [0x3A] = "Delete", [0x3B] = "Delete2", [0x3C] = "DefineLocal", [0x3D] = "CallFunction",
        [0x3E] = "Return", [0x3F] = "Modulo", [0x40] = "NewObject", [0x41] = "DefineLocal2", [0x42] = "InitArray",
        [0x43] = "InitObject", [0x44] = "TypeOf", [0x45] = "TargetPath", [0x46] = "Enumerate", [0x47] = "Add2", [0x48] = "Less2",
        [0x49] = "Equals2", [0x4A] = "ToNumber", [0x4B] = "ToString", [0x4C] = "PushDuplicate", [0x4D] = "StackSwap",
        [0x4E] = "GetMember", [0x4F] = "SetMember", [0x50] = "Increment", [0x51] = "Decrement", [0x52] = "CallMethod",
        [0x53] = "NewMethod", [0x54] = "InstanceOf", [0x55] = "Enumerate2", [0x60] = "BitAnd", [0x61] = "BitOr", [0x62] = "BitXor",
        [0x63] = "BitLShift", [0x64] = "BitRShift", [0x65] = "BitURShift", [0x66] = "StrictEquals", [0x67] = "Greater",
        [0x68] = "StringGreater", [0x69] = "Extends",
        [0x81] = "GotoFrame", [0x83] = "GetURL", [0x87] = "StoreRegister", [0x88] = "ConstantPool", [0x8A] = "WaitForFrame",
        [0x8B] = "SetTarget", [0x8C] = "GotoLabel", [0x8D] = "WaitForFrame2", [0x8E] = "DefineFunction2", [0x8F] = "Try",
        [0x94] = "With", [0x96] = "Push", [0x99] = "Jump", [0x9A] = "GetURL2", [0x9B] = "DefineFunction", [0x9D] = "If",
        [0x9E] = "Call", [0x9F] = "GotoFrame2",
    };
    static readonly Dictionary<string, int> Codes = Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ================================================================ Disassembler

    public static string Disassemble(byte[] code)
    {
        // 1. Anweisungsgrenzen und Sprungziele sammeln
        var starts = new HashSet<int>();
        var targets = new SortedSet<int>();
        int p = 0;
        while (p < code.Length)
        {
            starts.Add(p);
            int op = code[p];
            if (op == 0) break;
            if (op >= 0x80)
            {
                if (p + 3 > code.Length) break;
                int len = Bin.U16(code, p + 1), end = p + 3 + len;
                if (end > code.Length) break;
                if ((op == 0x99 || op == 0x9D) && len == 2) targets.Add(end + Bin.S16(code, p + 3));
                p = end;
            }
            else p++;
        }
        var labels = new Dictionary<int, string>();
        foreach (int t in targets) if (starts.Contains(t) || t == p) labels[t] = "L" + (labels.Count + 1);

        // 2. Ausgeben
        var sb = new StringBuilder();
        string[] pool = Array.Empty<string>();
        var blockEnds = new Stack<int>();
        p = 0;
        string Indent() => new string(' ', blockEnds.Count * 4);
        while (true)
        {
            while (blockEnds.Count > 0 && p >= blockEnds.Peek()) { blockEnds.Pop(); sb.Append(Indent()).Append("}\n"); }
            if (labels.TryGetValue(p, out var lab)) sb.Append(lab).Append(":\n");
            if (p >= code.Length) break;
            int op = code[p];
            if (op == 0)
            {
                sb.Append(Indent()).Append("End\n");
                p++;
                if (p < code.Length) sb.Append(".bytes ").Append(Hex(code, p, code.Length - p)).Append('\n');
                break;
            }
            if (op < 0x80)
            {
                sb.Append(Indent()).Append(Names.TryGetValue(op, out var n0) ? n0 : $"op 0x{op:X2}").Append('\n');
                p++;
                continue;
            }
            if (p + 3 > code.Length || p + 3 + Bin.U16(code, p + 1) > code.Length)
            {
                sb.Append(".bytes ").Append(Hex(code, p, code.Length - p)).Append('\n');
                break;
            }
            int len = Bin.U16(code, p + 1), q = p + 3, e = q + len;
            string line = Operand(code, op, q, e, len, pool, labels, out var newPool, out int blockSize);
            if (newPool != null) pool = newPool;
            sb.Append(Indent()).Append(line);
            p = e;
            if (blockSize >= 0)
            {
                sb.Append(" {\n");
                blockEnds.Push(p + blockSize);
            }
            else sb.Append('\n');
        }
        while (blockEnds.Count > 0) { blockEnds.Pop(); sb.Append(Indent()).Append("}\n"); }
        return sb.ToString();
    }

    static string Operand(byte[] a, int op, int q, int e, int len, string[] pool, Dictionary<int, string> labels, out string[] newPool, out int blockSize)
    {
        newPool = null; blockSize = -1;
        string name = Names.TryGetValue(op, out var n) ? n : null;
        try
        {
            switch (op)
            {
                case 0x81 when len == 2: return $"GotoFrame {Bin.U16(a, q)}";
                case 0x87 when len == 1: return $"StoreRegister {a[q]}";
                case 0x8A when len == 3: return $"WaitForFrame {Bin.U16(a, q)} {a[q + 2]}";
                case 0x8D when len == 1: return $"WaitForFrame2 {a[q]}";
                case 0x9F when len == 1 || len == 3:
                    return len == 1 ? $"GotoFrame2 {a[q]}" : $"GotoFrame2 {a[q]} bias {Bin.U16(a, q + 1)}";
                case 0x99 or 0x9D when len == 2:
                    {
                        int t = e + Bin.S16(a, q);
                        return $"{name} " + (labels.TryGetValue(t, out var l) ? l : $"@{Bin.S16(a, q)}");
                    }
                case 0x8B or 0x8C:
                    {
                        int r = q; var s = Str(a, ref r, e);
                        if (r == e) return $"{name} {s}";
                        break;
                    }
                case 0x83:
                    {
                        int r = q; var s1 = Str(a, ref r, e); var s2 = Str(a, ref r, e);
                        if (r == e) return $"GetURL {s1} {s2}";
                        break;
                    }
                case 0x9A when len == 1: return $"GetURL2 {a[q]}";
                case 0x88:
                    {
                        int cnt = Bin.U16(a, q), r = q + 2;
                        var list = new string[cnt]; var parts = new List<string>();
                        for (int i = 0; i < cnt; i++) { int s0 = r; parts.Add(Str(a, ref r, e)); list[i] = Raw(a, s0, r); }
                        if (r != e) break;
                        newPool = list;
                        return "ConstantPool" + (cnt > 0 ? " " + string.Join(" ", parts) : "");
                    }
                case 0x96:
                    {
                        var parts = new List<string>(); int r = q;
                        while (r < e)
                        {
                            int type = a[r++];
                            switch (type)
                            {
                                case 0:
                                    {
                                        int s0 = r; string lit = Str(a, ref r, e); string raw = Raw(a, s0, r);
                                        parts.Add(PoolIndex(pool, raw) >= 0 ? "@" + lit : lit);
                                        break;
                                    }
                                case 1: parts.Add(FloatText(BitConverter.ToSingle(a, r)) + "f"); r += 4; break;
                                case 2: parts.Add("null"); break;
                                case 3: parts.Add("undefined"); break;
                                case 4: parts.Add("r:" + a[r++]); break;
                                case 5: parts.Add(a[r++] != 0 ? "true" : "false"); break;
                                case 6:
                                    {
                                        var b = new byte[8]; Buffer.BlockCopy(a, r + 4, b, 0, 4); Buffer.BlockCopy(a, r, b, 4, 4);
                                        parts.Add(DoubleText(BitConverter.ToDouble(b, 0))); r += 8; break;
                                    }
                                case 7: parts.Add(Bin.S32(a, r).ToString(Inv)); r += 4; break;
                                case 8: case 9:
                                    {
                                        int i = type == 8 ? a[r] : Bin.U16(a, r); r += type == 8 ? 1 : 2;
                                        // Verweis darstellen; Index passt nicht zum erwarteten Typ → explizit
                                        bool natural = i < pool.Length && PoolIndex(pool, pool[i]) == i && (type == 8) == (i < 256);
                                        parts.Add(natural ? Quote(pool[i]) : (type == 8 ? "c:" : "c16:") + i);
                                        break;
                                    }
                                default: throw new FormatException();
                            }
                        }
                        if (r != e) break;
                        return "Push " + string.Join(", ", parts);
                    }
                case 0x9B:
                    {
                        int r = q; var fn = Str(a, ref r, e);
                        int np = Bin.U16(a, r); r += 2;
                        var ps = new List<string>();
                        for (int i = 0; i < np; i++) ps.Add(Str(a, ref r, e));
                        if (r + 2 != e) break;
                        blockSize = Bin.U16(a, r);
                        return $"DefineFunction {fn}" + (ps.Count > 0 ? " " + string.Join(" ", ps) : "") + " ()";
                    }
                case 0x94 when len == 2: blockSize = Bin.U16(a, q); return "With";
            }
        }
        catch (Exception) { }
        blockSize = -1;
        return $"op 0x{op:X2}" + (len > 0 ? " " + Hex(a, q, len) : "") + (name != null ? "   ; " + name : "");
    }

    static int PoolIndex(string[] pool, string raw) => Array.IndexOf(pool, raw);

    /// <summary>Zeichenkette als Rohtext (Bytes 1:1 in Latin-1), für Pool-Vergleiche.</summary>
    static string Raw(byte[] a, int s0, int r) => Encoding.Latin1.GetString(a, s0, Math.Max(0, r - s0 - 1));

    static string Str(byte[] a, ref int r, int e)
    {
        int s0 = r; while (r < e && a[r] != 0) r++;
        if (r >= e) throw new FormatException("unterminated string");
        string q = QuoteBytes(a, s0, r - s0);
        r++;
        return q;
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Bytes als Literal: gültiges UTF-8 als Zeichen, sonst \xNN je Byte.</summary>
    static string QuoteBytes(byte[] a, int o, int n)
    {
        var sb = new StringBuilder("\"");
        string s = null;
        try { s = StrictUtf8.GetString(a, o, n); } catch (DecoderFallbackException) { }
        if (s != null)
            foreach (char c in s) AppendChar(sb, c);
        else
            for (int i = 0; i < n; i++) { byte b = a[o + i]; if (b >= 0x20 && b < 0x7F) AppendChar(sb, (char)b); else sb.Append($"\\x{b:X2}"); }
        return sb.Append('"').ToString();
    }

    static string Quote(string raw) { var b = Encoding.Latin1.GetBytes(raw); return QuoteBytes(b, 0, b.Length); }

    static void AppendChar(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default: if (c < 0x20) sb.Append($"\\x{(int)c:X2}"); else sb.Append(c); break;
        }
    }

    static string DoubleText(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0 && double.IsNegative(d)) return "-0.0";
        string s = d.ToString("R", Inv);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }

    static string FloatText(float f) => float.IsNaN(f) ? "NaN" : f.ToString("R", Inv);

    static string Hex(byte[] a, int o, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(a[o + i].ToString("X2")); }
        return sb.ToString();
    }

    // ================================================================ Assembler
    public sealed class AsmError : Exception
    {
        public int Line { get; }
        public AsmError(int line, string msg) : base($"line {line}: {msg}") { Line = line; }
    }

    public static byte[] Assemble(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var o = new List<byte>();
        var labelPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fix = new List<(int at, string label, int line)>();       // 16-Bit-Sprungweite an „at“, relativ zu at+2
        var blocks = new Stack<(int sizeAt, int line)>();               // offene Funktions-/With-Blöcke
        string[] pool = Array.Empty<string>();

        for (int ln = 0; ln < lines.Length; ln++)
        {
            int lineNo = ln + 1;
            var tok = Tokenize(lines[ln], lineNo);
            if (tok.Count == 0) continue;
            // Blockende
            while (tok.Count > 0 && tok[0] == "}")
            {
                if (blocks.Count == 0) throw new AsmError(lineNo, "\"}\" without an open block");
                var (sizeAt, _) = blocks.Pop();
                int size = o.Count - (sizeAt + 2);
                if (size > 0xFFFF) throw new AsmError(lineNo, "block larger than 64 KB");
                o[sizeAt] = (byte)size; o[sizeAt + 1] = (byte)(size >> 8);
                tok.RemoveAt(0);
            }
            if (tok.Count == 0) continue;
            // Sprungmarke
            if (tok[0].EndsWith(':') && tok[0].Length > 1 && !tok[0].StartsWith('"'))
            {
                string l = tok[0][..^1];
                if (!labelPos.TryAdd(l, o.Count)) throw new AsmError(lineNo, $"label {l} defined twice");
                tok.RemoveAt(0);
                if (tok.Count == 0) continue;
            }
            string m = tok[0];
            var args = tok.Skip(1).Where(t => t != ",").ToList();
            bool opensBlock = args.Count > 0 && args[^1] == "{";
            if (opensBlock) args.RemoveAt(args.Count - 1);

            if (m == ".bytes") { foreach (var h in args) o.Add(HexByte(h, lineNo)); continue; }
            if (m.Equals("op", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count == 0) throw new AsmError(lineNo, "opcode missing");
                int code = HexByte(args[0], lineNo);
                var body = args.Skip(1).Select(h => HexByte(h, lineNo)).ToList();
                o.Add((byte)code);
                if (code >= 0x80) { W16(o, body.Count); o.AddRange(body); }
                else if (body.Count > 0) throw new AsmError(lineNo, "opcodes below 0x80 take no data");
                continue;
            }
            if (!Codes.TryGetValue(m, out int opc)) throw new AsmError(lineNo, $"unknown instruction \"{m}\"");
            if (opc < 0x80)
            {
                if (args.Count > 0) throw new AsmError(lineNo, $"{m} takes no arguments");
                o.Add((byte)opc);
                continue;
            }
            var d = new List<byte>();
            int blockSizeAt = -1;
            switch (opc)
            {
                case 0x81: Need(args, 1, lineNo, m); W16(d, Int(args[0], lineNo, 0, 0xFFFF)); break;
                case 0x87: Need(args, 1, lineNo, m); d.Add((byte)Int(args[0], lineNo, 0, 255)); break;
                case 0x8A: Need(args, 2, lineNo, m); W16(d, Int(args[0], lineNo, 0, 0xFFFF)); d.Add((byte)Int(args[1], lineNo, 0, 255)); break;
                case 0x8D: Need(args, 1, lineNo, m); d.Add((byte)Int(args[0], lineNo, 0, 255)); break;
                case 0x9A: Need(args, 1, lineNo, m); d.Add((byte)Int(args[0], lineNo, 0, 255)); break;
                case 0x9F:
                    if (args.Count == 1) d.Add((byte)Int(args[0], lineNo, 0, 255));
                    else if (args.Count == 3 && args[1].Equals("bias", StringComparison.OrdinalIgnoreCase)) { d.Add((byte)Int(args[0], lineNo, 0, 255)); W16(d, Int(args[2], lineNo, 0, 0xFFFF)); }
                    else throw new AsmError(lineNo, "usage: GotoFrame2 <flags> [bias <n>]");
                    break;
                case 0x8B: case 0x8C: Need(args, 1, lineNo, m); d.AddRange(StrBytes(args[0], lineNo)); break;
                case 0x83: Need(args, 2, lineNo, m); d.AddRange(StrBytes(args[0], lineNo)); d.AddRange(StrBytes(args[1], lineNo)); break;
                case 0x88:
                    {
                        W16(d, args.Count);
                        var list = new string[args.Count];
                        for (int i = 0; i < args.Count; i++) { var b = StrBytes(args[i], lineNo); d.AddRange(b); list[i] = Encoding.Latin1.GetString(b, 0, b.Length - 1); }
                        pool = list;
                        break;
                    }
                case 0x96:
                    if (args.Count == 0) throw new AsmError(lineNo, "Push without values");
                    foreach (var v in args) PushValue(d, v, pool, lineNo);
                    break;
                case 0x99: case 0x9D:
                    {
                        Need(args, 1, lineNo, m);
                        o.Add((byte)opc); W16(o, 2);
                        if (args[0].StartsWith('@')) W16(o, Int(args[0][1..], lineNo, short.MinValue, short.MaxValue));
                        else { fix.Add((o.Count, args[0], lineNo)); W16(o, 0); }
                        continue;
                    }
                case 0x9B:
                    {
                        if (!opensBlock) throw new AsmError(lineNo, "DefineFunction needs \"{\" at the end of the line");
                        var ps = args.Where(x => x != "()").ToList();
                        if (ps.Count == 0) throw new AsmError(lineNo, "function name missing (use \"\" for anonymous)");
                        d.AddRange(StrBytes(ps[0], lineNo));
                        W16(d, ps.Count - 1);
                        foreach (var x in ps.Skip(1)) d.AddRange(StrBytes(x, lineNo));
                        blockSizeAt = d.Count; W16(d, 0);
                        break;
                    }
                case 0x94:
                    if (!opensBlock || args.Count != 0) throw new AsmError(lineNo, "usage: With {");
                    blockSizeAt = 0; W16(d, 0);
                    break;
                default: throw new AsmError(lineNo, $"{m}: write it as \"op 0x{opc:X2} …\" (raw bytes)");
            }
            if (opensBlock && blockSizeAt < 0) throw new AsmError(lineNo, $"{m} does not open a block");
            if (d.Count > 0xFFFF) throw new AsmError(lineNo, "instruction too long");
            o.Add((byte)opc); W16(o, d.Count);
            int dataStart = o.Count;
            o.AddRange(d);
            if (blockSizeAt >= 0) blocks.Push((dataStart + blockSizeAt, lineNo));
        }
        if (blocks.Count > 0) throw new AsmError(blocks.Peek().line, "block is not closed with \"}\"");
        foreach (var (at, label, line) in fix)
        {
            if (!labelPos.TryGetValue(label, out int t)) throw new AsmError(line, $"label {label} not found");
            int off = t - (at + 2);
            if (off < short.MinValue || off > short.MaxValue) throw new AsmError(line, "jump too far");
            o[at] = (byte)off; o[at + 1] = (byte)(off >> 8);
        }
        return o.ToArray();
    }

    static void Need(List<string> a, int n, int line, string m) { if (a.Count != n) throw new AsmError(line, $"{m} expects {n} argument(s)"); }

    static void W16(List<byte> d, int v) { d.Add((byte)v); d.Add((byte)(v >> 8)); }

    static byte HexByte(string s, int line)
    {
        var t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        if (t.Length is 1 or 2 && byte.TryParse(t, NumberStyles.HexNumber, Inv, out byte b)) return b;
        throw new AsmError(line, $"\"{s}\" is not a hex byte");
    }

    static int Int(string s, int line, int min, int max)
    {
        if (int.TryParse(s, NumberStyles.Integer, Inv, out int v) && v >= min && v <= max) return v;
        throw new AsmError(line, $"\"{s}\": integer {min}…{max} expected");
    }

    static void PushValue(List<byte> d, string v, string[] pool, int line)
    {
        if (v.StartsWith('"') || v.StartsWith("@\""))
        {
            bool inline = v[0] == '@';
            var b = StrBytes(inline ? v[1..] : v, line);
            int i = inline ? -1 : Array.IndexOf(pool, Encoding.Latin1.GetString(b, 0, b.Length - 1));
            if (i >= 0 && i < 256) { d.Add(8); d.Add((byte)i); }
            else if (i >= 256) { d.Add(9); W16(d, i); }
            else { d.Add(0); d.AddRange(b); }
            return;
        }
        switch (v)
        {
            case "null": d.Add(2); return;
            case "undefined": d.Add(3); return;
            case "true": d.Add(5); d.Add(1); return;
            case "false": d.Add(5); d.Add(0); return;
        }
        if (v.StartsWith("r:")) { d.Add(4); d.Add((byte)Int(v[2..], line, 0, 255)); return; }
        if (v.StartsWith("c:")) { d.Add(8); d.Add((byte)Int(v[2..], line, 0, 255)); return; }
        if (v.StartsWith("c16:")) { d.Add(9); W16(d, Int(v[4..], line, 0, 0xFFFF)); return; }
        if (v.EndsWith('f') && !v.EndsWith("Inf", StringComparison.Ordinal))
        {
            string fs = v[..^1];
            float f = fs == "NaN" ? float.NaN : float.Parse(fs, NumberStyles.Float, Inv);
            d.Add(1); d.AddRange(BitConverter.GetBytes(f)); return;
        }
        bool isDouble = v.Contains('.') || v.Contains('E') || v.Contains('e') || v is "NaN" or "Infinity" or "-Infinity";
        if (!isDouble && int.TryParse(v, NumberStyles.Integer, Inv, out int iv)) { d.Add(7); d.AddRange(BitConverter.GetBytes(iv)); return; }
        double dv = v switch { "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity, _ => double.NaN };
        if (v is not ("NaN" or "Infinity" or "-Infinity") && !double.TryParse(v, NumberStyles.Float, Inv, out dv))
            throw new AsmError(line, $"Push: cannot parse \"{v}\"");
        var bytes = BitConverter.GetBytes(dv);
        d.Add(6); d.AddRange(bytes.Skip(4).Take(4)); d.AddRange(bytes.Take(4));   // AVM1: obere Hälfte zuerst
    }

    /// <summary>Literal „"…"“ → Bytes mit abschließender 0. \xNN = Rohbyte, sonst UTF-8.</summary>
    static byte[] StrBytes(string lit, int line)
    {
        if (lit.Length < 2 || lit[0] != '"' || lit[^1] != '"') throw new AsmError(line, $"string in \"…\" expected, found: {lit}");
        var o = new List<byte>();
        var tmp = new StringBuilder();
        void Flush() { if (tmp.Length > 0) { o.AddRange(Encoding.UTF8.GetBytes(tmp.ToString())); tmp.Clear(); } }
        for (int i = 1; i < lit.Length - 1; i++)
        {
            char c = lit[i];
            if (c != '\\') { tmp.Append(c); continue; }
            char n = lit[++i];
            switch (n)
            {
                case 'n': tmp.Append('\n'); break;
                case 'r': tmp.Append('\r'); break;
                case 't': tmp.Append('\t'); break;
                case 'x':
                    Flush();
                    o.Add(byte.Parse(lit.AsSpan(i + 1, 2), NumberStyles.HexNumber, Inv)); i += 2; break;
                default: tmp.Append(n); break;
            }
        }
        Flush();
        if (o.Contains(0)) throw new AsmError(line, "string must not contain \\x00");
        o.Add(0);
        return o.ToArray();
    }

    /// <summary>Zeile in Wörter, Zeichenketten (mit Escapes, optional mit @ davor), „,“ „{“ „}“ zerlegen; „;“ = Kommentar.</summary>
    static List<string> Tokenize(string s, int line)
    {
        var r = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ';') break;
            if (c is ',' or '{' or '}') { r.Add(c.ToString()); i++; continue; }
            if (c == '"' || (c == '@' && i + 1 < s.Length && s[i + 1] == '"'))
            {
                int st = i; if (c == '@') i++;
                i++;
                while (i < s.Length && s[i] != '"') { if (s[i] == '\\') i++; i++; }
                if (i >= s.Length) throw new AsmError(line, "unterminated string");
                i++;
                r.Add(s[st..i]);
                continue;
            }
            int s0 = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not (',' or '{' or '}' or ';' or '"')) i++;
            r.Add(s[s0..i]);
        }
        return r;
    }

    // ================================================================ Hilfen für Editor
    /// <summary>Enthält der Block ein Stop() auf oberster Ebene (Funktionsrümpfe übersprungen)?</summary>
    public static bool HasStop(byte[] code)
    {
        int p = 0;
        while (p < code.Length)
        {
            int op = code[p];
            if (op == 0) return false;
            if (op == 0x07) return true;
            if (op < 0x80) { p++; continue; }
            if (p + 3 > code.Length) return false;
            int len = Bin.U16(code, p + 1), e = p + 3 + len;
            if (op == 0x9B && len >= 2 && e <= code.Length) e += Bin.U16(code, e - 2);
            p = e;
        }
        return false;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VmfModel.Timeline, StrongBox<int>> _stops = new();
    sealed class StrongBox<T> { public T Value; }

    /// <summary>
    /// Bild, das ein verschachteltes Sprite in der Ansicht zeigt: von allen Bildern mit stop() (ohne stop(): alle Bildern)
    /// das mit den meisten Elementen – so erscheinen Menüs, die per Skript erst eingeblendet werden, vollständig. -1 = nur ein Bild.
    /// </summary>
    public static int StopFrame(VmfModel.Timeline tl)
    {
        if (_stops.TryGetValue(tl, out var b)) return b.Value;
        int n = tl.Frames.Count, best = -1, bestCount = -1;
        var stops = new bool[n]; bool any = false;
        for (int f = 0; f < n; f++)
            foreach (var op in tl.Frames[f])
                if (op.Kind == VmfModel.OpKind.Action && op.Actions != null && HasStop(op.Actions)) { stops[f] = true; any = true; break; }
        var depths = new HashSet<int>();
        for (int f = 0; f < n; f++)
        {
            foreach (var op in tl.Frames[f])
                if (op.Kind == VmfModel.OpKind.Place && op.HasChar) depths.Add(op.Depth);
                else if (op.Kind == VmfModel.OpKind.Remove) depths.Remove(op.Depth);
            if ((stops[f] || !any) && depths.Count > bestCount) { best = f; bestCount = depths.Count; }
        }
        int r = n <= 1 ? -1 : best;
        _stops.AddOrUpdate(tl, new StrongBox<int> { Value = r });
        return r;
    }

    /// <summary>Kurze Vorschau: GotoLabel/GetURL/Funktionsnamen und Zeichenketten eines Blocks (für Listen und Suche).</summary>
    public static string Summary(byte[] code, int max = 120)
    {
        string text;
        try { text = Disassemble(code); } catch { return $"{code.Length} bytes"; }
        var keys = new List<string>();
        foreach (var l in text.Split('\n'))
        {
            var t = l.Trim();
            if (t.StartsWith("GotoLabel") || t.StartsWith("GetURL ") || t.StartsWith("DefineFunction") || t == "Stop" || t == "Play")
                keys.Add(t.Replace(" ()", "").Replace(" {", ""));
        }
        var s = string.Join(" · ", keys);
        return s.Length > max ? s[..max] + "…" : s;
    }
}
