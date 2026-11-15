namespace VmfCore;

/// <summary>
/// CPU-Rasterung einer VMF-Shape wie im Spiel-Nachbau (Client/…/Flash/VmfRaster.cs): even-odd je Füllstil über alle Kanten
/// mit fill0 oder fill1 = Stil, 2x2 Supersampling, Linien als Rechtecke. Ergebnis BGRA vormultipliziert (für WPF Pbgra32).
/// </summary>
public static class ShapeRaster
{
    public sealed class Result { public int Width, Height; public byte[] Bgra = Array.Empty<byte>(); public float OriginX, OriginY, Scale; }

    const int SS = 2;

    public static Result Render(VmfModel.Shape s, float scale, Func<int, (PixelImage img, VmfModel.Bitmap bmp)> bitmap)
    {
        var bnd = s.Bounds;
        int W = Math.Max(1, (int)MathF.Ceiling(bnd.W * scale) + 2), H = Math.Max(1, (int)MathF.Ceiling(bnd.H * scale) + 2);
        if ((long)W * H > 4_000_000) { float k = MathF.Sqrt(4_000_000f / ((long)W * H)); scale *= k; W = (int)MathF.Ceiling(bnd.W * scale) + 2; H = (int)MathF.Ceiling(bnd.H * scale) + 2; }
        float ox = bnd.X - 1f / scale, oy = bnd.Y - 1f / scale;
        int N = W * H;
        var acc = new float[N * 4];   // vormultipliziert r,g,b,a

        var groups = new Dictionary<int, List<(float, float, float, float)>>();
        foreach (var p in s.Paths)
            for (int side = 0; side < 2; side++)
            {
                int f = side == 0 ? p.Fill0 : p.Fill1;
                if (f <= 0 || f > s.Fills.Count) continue;
                if (side == 1 && p.Fill1 == p.Fill0) continue;
                if (!groups.TryGetValue(f, out var list)) groups[f] = list = new();
                for (int i = 0; i + 1 < p.Points.Count; i++) list.Add((p.Points[i].X, p.Points[i].Y, p.Points[i + 1].X, p.Points[i + 1].Y));
            }
        var cov = new float[N];
        foreach (var kv in groups)
        {
            Array.Clear(cov);
            if (!Coverage(kv.Value, W, H, ox, oy, scale, cov, false)) continue;
            Composite(acc, cov, W, H, ox, oy, scale, s.Fills[kv.Key - 1], bitmap);
        }
        foreach (var p in s.Paths)
        {
            if (p.Line <= 0 || p.Line > s.Lines.Count) continue;
            var ls = s.Lines[p.Line - 1];
            float hw = MathF.Max(ls.Width, 1f / scale) * 0.5f;
            Array.Clear(cov);
            var quad = new List<(float, float, float, float)>(4);
            for (int i = 0; i + 1 < p.Points.Count; i++)
            {
                var a = p.Points[i]; var b = p.Points[i + 1]; var dir = b - a;
                float l = dir.Length; if (l < 1e-5f) continue;
                var n = new Vec2(-dir.Y, dir.X) * (hw / l);
                var q0 = a + n; var q1 = b + n; var q2 = b - n; var q3 = a - n;
                quad.Clear();
                quad.Add((q0.X, q0.Y, q1.X, q1.Y)); quad.Add((q1.X, q1.Y, q2.X, q2.Y)); quad.Add((q2.X, q2.Y, q3.X, q3.Y)); quad.Add((q3.X, q3.Y, q0.X, q0.Y));
                Coverage(quad, W, H, ox, oy, scale, cov, true);
            }
            Composite(acc, cov, W, H, ox, oy, scale, new VmfModel.FillStyle { Type = 0, Color = ls.Color }, bitmap);
        }
        var px = new byte[N * 4];
        for (int i = 0; i < N; i++)
        {
            float a = acc[i * 4 + 3]; if (a <= 1e-4f) continue;
            px[i * 4] = (byte)Math.Clamp(acc[i * 4 + 2] * 255f, 0, 255);   // B (vormultipliziert)
            px[i * 4 + 1] = (byte)Math.Clamp(acc[i * 4 + 1] * 255f, 0, 255);
            px[i * 4 + 2] = (byte)Math.Clamp(acc[i * 4] * 255f, 0, 255);
            px[i * 4 + 3] = (byte)Math.Clamp(a * 255f, 0, 255);
        }
        return new Result { Width = W, Height = H, Bgra = px, OriginX = ox, OriginY = oy, Scale = scale };
    }

    static bool Coverage(List<(float x0, float y0, float x1, float y1)> edges, int W, int H, float ox, float oy, float sc, float[] cov, bool union)
    {
        int n = edges.Count; if (n == 0) return false;
        float s = sc * SS;
        var x0 = new float[n]; var y0 = new float[n]; var x1 = new float[n]; var y1 = new float[n];
        float ymin = float.MaxValue, ymax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var e = edges[i];
            x0[i] = (e.x0 - ox) * s; y0[i] = (e.y0 - oy) * s; x1[i] = (e.x1 - ox) * s; y1[i] = (e.y1 - oy) * s;
            ymin = MathF.Min(ymin, MathF.Min(y0[i], y1[i])); ymax = MathF.Max(ymax, MathF.Max(y0[i], y1[i]));
        }
        int Wss = W * SS, Hss = H * SS;
        int r0 = Math.Max(0, (int)MathF.Floor(ymin)), r1 = Math.Min(Hss - 1, (int)MathF.Ceiling(ymax));
        var xs = new List<float>(16);
        int N = W * H;
        var local = union ? new float[N] : cov;
        const float w = 1f / (SS * SS);
        bool any = false;
        for (int row = r0; row <= r1; row++)
        {
            float yc = row + 0.5f;
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                float a = y0[i], b = y1[i];
                if ((a <= yc && b > yc) || (b <= yc && a > yc)) xs.Add(x0[i] + (yc - a) * (x1[i] - x0[i]) / (b - a));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            int py = row / SS;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int a = Math.Max(0, (int)MathF.Ceiling(xs[k] - 0.5f)), b = Math.Min(Wss, (int)MathF.Ceiling(xs[k + 1] - 0.5f));
                for (int sx = a; sx < b; sx++) local[py * W + sx / SS] += w;
                if (b > a) any = true;
            }
        }
        if (union) for (int i = 0; i < N; i++) cov[i] = MathF.Max(cov[i], MathF.Min(1f, local[i]));
        return any;
    }

    static void Composite(float[] acc, float[] cov, int W, int H, float ox, float oy, float sc, VmfModel.FillStyle fs,
        Func<int, (PixelImage img, VmfModel.Bitmap bmp)> bitmap)
    {
        var inv = Matrix2D.Identity;
        PixelImage img = null; VmfModel.Bitmap bmp = null;
        bool grad = fs.Type == 0x10 || fs.Type == 0x12 || fs.Type == 0x13, bm = fs.Type >= 0x40;
        if (grad || bm)
        {
            var m = fs.Matrix; m.A /= 20f; m.B /= 20f; m.C /= 20f; m.D /= 20f;
            inv = m.Inverse();
            if (bm)
            {
                if (bitmap != null) { var r = bitmap(fs.BitmapId); img = r.img; bmp = r.bmp; }
                if (img == null || bmp == null) return;
            }
        }
        bool repeat = fs.Type == 0x40 || fs.Type == 0x42;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float c = cov[y * W + x]; if (c <= 0f) continue;
                if (c > 1f) c = 1f;
                Rgba col;
                if (fs.Type == 0) col = fs.Color;
                else
                {
                    var uv = inv.Apply(new Vec2((x + 0.5f) / sc + ox, (y + 0.5f) / sc + oy));
                    if (grad)
                    {
                        float g = fs.Type == 0x10 ? (uv.X + 16384f) / 32768f : MathF.Sqrt(uv.X * uv.X + uv.Y * uv.Y) / 16384f;
                        col = Gradient(fs, Math.Clamp(g, 0f, 1f) * 255f);
                    }
                    else
                    {
                        float bw = Math.Max(1, bmp.Width), bh = Math.Max(1, bmp.Height);
                        float u = uv.X / bw, v = uv.Y / bh;
                        if (repeat) { u -= MathF.Floor(u); v -= MathF.Floor(v); } else { u = Math.Clamp(u, 0f, 1f); v = Math.Clamp(v, 0f, 1f); }
                        col = img.Sample((bmp.U0 + (bmp.U1 - bmp.U0) * u) * img.Width, (bmp.V0 + (bmp.V1 - bmp.V0) * v) * img.Height);
                    }
                }
                float a = col.A / 255f * c;
                int o = (y * W + x) * 4;
                acc[o] = col.R / 255f * a + acc[o] * (1 - a);
                acc[o + 1] = col.G / 255f * a + acc[o + 1] * (1 - a);
                acc[o + 2] = col.B / 255f * a + acc[o + 2] * (1 - a);
                acc[o + 3] = a + acc[o + 3] * (1 - a);
            }
    }

    static Rgba Gradient(VmfModel.FillStyle fs, float r)
    {
        var rs = fs.Ratios; var cs = fs.Colors;
        if (rs.Length == 0) return new Rgba(0, 0, 0, 0);
        if (r <= rs[0]) return cs[0];
        for (int i = 1; i < rs.Length; i++)
            if (r <= rs[i]) return Rgba.Lerp(cs[i - 1], cs[i], (r - rs[i - 1]) / MathF.Max(1f, rs[i] - rs[i - 1]));
        return cs[^1];
    }
}
