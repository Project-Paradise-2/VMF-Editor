namespace VmfCore;

public struct Vec2
{
    public float X, Y;
    public Vec2(float x, float y) { X = x; Y = y; }
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float k) => new(a.X * k, a.Y * k);
    public float Length => MathF.Sqrt(X * X + Y * Y);
}

public struct RectF
{
    public float X, Y, W, H;
    public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    public float XMax => X + W;
    public float YMax => Y + H;
    public bool IsEmpty => W <= 0 && H <= 0;
    public static RectF FromMinMax(float x0, float y0, float x1, float y1) => new(x0, y0, x1 - x0, y1 - y0);
    public RectF Union(RectF o)
    {
        if (IsEmpty) return o; if (o.IsEmpty) return this;
        return FromMinMax(MathF.Min(X, o.X), MathF.Min(Y, o.Y), MathF.Max(XMax, o.XMax), MathF.Max(YMax, o.YMax));
    }
    public bool Contains(Vec2 p) => p.X >= X && p.X <= XMax && p.Y >= Y && p.Y <= YMax;
}

public struct Rgba
{
    public byte R, G, B, A;
    public Rgba(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
    public static Rgba Lerp(Rgba a, Rgba b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    }
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

/// <summary>2D-Affinmatrix wie SWF: x' = A x + C y + Tx, y' = B x + D y + Ty (Tx/Ty in Pixeln).</summary>
public struct Matrix2D
{
    public float A, B, C, D, Tx, Ty;
    public static readonly Matrix2D Identity = new() { A = 1, D = 1 };
    public static Matrix2D operator *(Matrix2D m, Matrix2D n) => new()
    {
        A = m.A * n.A + m.C * n.B, B = m.B * n.A + m.D * n.B,
        C = m.A * n.C + m.C * n.D, D = m.B * n.C + m.D * n.D,
        Tx = m.A * n.Tx + m.C * n.Ty + m.Tx, Ty = m.B * n.Tx + m.D * n.Ty + m.Ty,
    };
    public Vec2 Apply(Vec2 p) => new(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);
    public float Det => A * D - B * C;
    public Matrix2D Inverse()
    {
        float det = Det; if (MathF.Abs(det) < 1e-12f) return Identity;
        float i = 1f / det;
        return new() { A = D * i, B = -B * i, C = -C * i, D = A * i, Tx = (C * Ty - D * Tx) * i, Ty = (B * Tx - A * Ty) * i };
    }
    public RectF Transform(RectF r)
    {
        var a = Apply(new(r.X, r.Y)); var b = Apply(new(r.XMax, r.Y)); var c = Apply(new(r.X, r.YMax)); var d = Apply(new(r.XMax, r.YMax));
        return RectF.FromMinMax(MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
                                MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)), MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
    }

    // ---- Zerlegung für die Bearbeitung (Position, Skalierung, Drehung; Scherung bleibt erhalten, wenn nichts davon geändert wird)
    public float ScaleX => MathF.Sqrt(A * A + B * B);
    public float ScaleY => (Det < 0 ? -1f : 1f) * MathF.Sqrt(C * C + D * D);
    public float RotationDeg => MathF.Atan2(B, A) * 180f / MathF.PI;
    public static Matrix2D Compose(float x, float y, float sx, float sy, float rotDeg)
    {
        float r = rotDeg * MathF.PI / 180f, c = MathF.Cos(r), s = MathF.Sin(r);
        return new() { A = c * sx, B = s * sx, C = -s * sy, D = c * sy, Tx = x, Ty = y };
    }
}

/// <summary>Farbtransformation (CXFORMWITHALPHA) in Rohwerten: Mul in 1/256 (256 = 1.0), Add -255..255.</summary>
public struct CxForm
{
    public int Mr, Mg, Mb, Ma, Ar, Ag, Ab, Aa;
    public static readonly CxForm Identity = new() { Mr = 256, Mg = 256, Mb = 256, Ma = 256 };
    public bool IsIdentity => Mr == 256 && Mg == 256 && Mb == 256 && Ma == 256 && Ar == 0 && Ag == 0 && Ab == 0 && Aa == 0;
    public float Alpha => Ma / 256f;
    /// <summary>Verkettung (Eltern * Kind) als Gleitkomma für die Darstellung.</summary>
    public static (float[] mul, float[] add) Combine((float[] mul, float[] add) parent, CxForm c)
    {
        var m = new float[4]; var a = new float[4];
        int[] cm = { c.Mr, c.Mg, c.Mb, c.Ma }, ca = { c.Ar, c.Ag, c.Ab, c.Aa };
        for (int i = 0; i < 4; i++) { m[i] = parent.mul[i] * cm[i] / 256f; a[i] = parent.mul[i] * ca[i] / 255f + parent.add[i]; }
        return (m, a);
    }
    public static (float[] mul, float[] add) IdentityF => (new float[] { 1, 1, 1, 1 }, new float[4]);
}

/// <summary>Bit-Leser für SWF-Strukturen.</summary>
public sealed class BitReader
{
    readonly byte[] _d; int _p, _b;
    public BitReader(byte[] d, int p) { _d = d; _p = p; }
    public int Pos { get { Align(); return _p; } set { _p = value; _b = 0; } }
    public void Align() { if (_b != 0) { _b = 0; _p++; } }
    public int UB(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) { v = (v << 1) | ((_d[_p] >> (7 - _b)) & 1); if (++_b == 8) { _b = 0; _p++; } }
        return v;
    }
    public int SB(int n) { if (n == 0) return 0; int v = UB(n); return (v & (1 << (n - 1))) != 0 ? v - (1 << n) : v; }
    public int U8() { Align(); return _d[_p++]; }
    public int U16() { Align(); int v = _d[_p] | (_d[_p + 1] << 8); _p += 2; return v; }
    public RectF Rect()
    {
        int n = UB(5); float x0 = SB(n) / 20f, x1 = SB(n) / 20f, y0 = SB(n) / 20f, y1 = SB(n) / 20f; Align();
        return RectF.FromMinMax(x0, y0, x1, y1);
    }
    public Matrix2D Matrix()
    {
        var m = Matrix2D.Identity;
        if (UB(1) == 1) { int n = UB(5); m.A = SB(n) / 65536f; m.D = SB(n) / 65536f; }
        if (UB(1) == 1) { int n = UB(5); m.B = SB(n) / 65536f; m.C = SB(n) / 65536f; }
        int t = UB(5); m.Tx = SB(t) / 20f; m.Ty = SB(t) / 20f;
        Align();
        return m;
    }
    public CxForm CxForm(bool alpha)
    {
        var c = VmfCore.CxForm.Identity;
        int ha = UB(1), hm = UB(1), n = UB(4);
        if (hm == 1) { c.Mr = SB(n); c.Mg = SB(n); c.Mb = SB(n); if (alpha) c.Ma = SB(n); }
        if (ha == 1) { c.Ar = SB(n); c.Ag = SB(n); c.Ab = SB(n); if (alpha) c.Aa = SB(n); }
        Align();
        return c;
    }
    public Rgba Rgb(bool alpha)
    {
        Align();
        var c = new Rgba(_d[_p], _d[_p + 1], _d[_p + 2], alpha ? _d[_p + 3] : (byte)255);
        _p += alpha ? 4 : 3;
        return c;
    }
}

/// <summary>Bit-Schreiber (Gegenstück zu BitReader): kleinste nötige Bitbreiten wie der Flash-Compiler.</summary>
public sealed class BitWriter
{
    readonly List<byte> _o = new();
    int _cur, _b;
    public void UB(int n, int v) { for (int i = n - 1; i >= 0; i--) { _cur = (_cur << 1) | ((v >> i) & 1); if (++_b == 8) { _o.Add((byte)_cur); _cur = 0; _b = 0; } } }
    public void SB(int n, int v) => UB(n, v & (n >= 32 ? -1 : (1 << n) - 1));
    public void Align() { if (_b > 0) { _cur <<= 8 - _b; _o.Add((byte)_cur); _cur = 0; _b = 0; } }
    public void U8(int v) { Align(); _o.Add((byte)v); }
    public void U16(int v) { Align(); _o.Add((byte)v); _o.Add((byte)(v >> 8)); }
    public void U32(uint v) { Align(); _o.Add((byte)v); _o.Add((byte)(v >> 8)); _o.Add((byte)(v >> 16)); _o.Add((byte)(v >> 24)); }
    public void Bytes(byte[] b) { Align(); _o.AddRange(b); }
    public void CString(string s) { Align(); _o.AddRange(System.Text.Encoding.Latin1.GetBytes(s ?? "")); _o.Add(0); }
    public byte[] ToArray() { Align(); return _o.ToArray(); }

    public static int BitsS(params int[] vs)
    {
        int n = 1;
        foreach (var v in vs) { int a = v < 0 ? ~v : v; int k = 1; while (a > 0) { k++; a >>= 1; } n = Math.Max(n, k); }
        return n;
    }

    public void Rect(RectF r)
    {
        int x0 = (int)MathF.Round(r.X * 20), x1 = (int)MathF.Round(r.XMax * 20), y0 = (int)MathF.Round(r.Y * 20), y1 = (int)MathF.Round(r.YMax * 20);
        int n = BitsS(x0, x1, y0, y1);
        UB(5, n); SB(n, x0); SB(n, x1); SB(n, y0); SB(n, y1); Align();
    }

    public void Matrix(Matrix2D m)
    {
        int a = (int)MathF.Round(m.A * 65536), d = (int)MathF.Round(m.D * 65536), b = (int)MathF.Round(m.B * 65536), c = (int)MathF.Round(m.C * 65536);
        int tx = (int)MathF.Round(m.Tx * 20), ty = (int)MathF.Round(m.Ty * 20);
        bool hasScale = a != 65536 || d != 65536, hasRot = b != 0 || c != 0;
        UB(1, hasScale ? 1 : 0);
        if (hasScale) { int n = BitsS(a, d); UB(5, n); SB(n, a); SB(n, d); }
        UB(1, hasRot ? 1 : 0);
        if (hasRot) { int n = BitsS(b, c); UB(5, n); SB(n, b); SB(n, c); }
        int nt = tx == 0 && ty == 0 ? 0 : BitsS(tx, ty);
        UB(5, nt); if (nt > 0) { SB(nt, tx); SB(nt, ty); }
        Align();
    }

    public void CxForm(CxForm c, bool alpha)
    {
        bool hm = c.Mr != 256 || c.Mg != 256 || c.Mb != 256 || (alpha && c.Ma != 256);
        bool ha = c.Ar != 0 || c.Ag != 0 || c.Ab != 0 || (alpha && c.Aa != 0);
        var vals = new List<int>();
        if (hm) { vals.AddRange(new[] { c.Mr, c.Mg, c.Mb }); if (alpha) vals.Add(c.Ma); }
        if (ha) { vals.AddRange(new[] { c.Ar, c.Ag, c.Ab }); if (alpha) vals.Add(c.Aa); }
        int n = vals.Count == 0 ? 1 : Math.Min(15, BitsS(vals.ToArray()));
        UB(1, ha ? 1 : 0); UB(1, hm ? 1 : 0); UB(4, n);
        if (hm) { SB(n, c.Mr); SB(n, c.Mg); SB(n, c.Mb); if (alpha) SB(n, c.Ma); }
        if (ha) { SB(n, c.Ar); SB(n, c.Ag); SB(n, c.Ab); if (alpha) SB(n, c.Aa); }
        Align();
    }
}
