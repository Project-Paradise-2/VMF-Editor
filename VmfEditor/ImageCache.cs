using System.Windows.Media;
using System.Windows.Media.Imaging;
using VmfCore;

namespace VmfEditor;

/// <summary>WPF-Bilder zu den Charakteren: gerasterte Shapes je Zoomstufe (Zweierpotenz), eingefärbte Varianten, Font-Masken.</summary>
public sealed class ImageCache
{
    readonly BankAssets _a;
    readonly Dictionary<(int id, float scale), (BitmapSource bmp, ShapeRaster.Result r)> _shapes = new();
    readonly Dictionary<(int id, float scale, int cx), BitmapSource> _tinted = new();
    readonly Dictionary<int, BitmapSource> _fonts = new();

    public ImageCache(BankAssets a) { _a = a; }

    public void Clear() { _shapes.Clear(); _tinted.Clear(); _fonts.Clear(); }

    /// <summary>Gerasterte Shape (vormultipliziert) für die gewünschte Bildschirmskalierung.</summary>
    public (BitmapSource bmp, ShapeRaster.Result r) Shape(VmfModel.Shape s, float screenScale)
    {
        float scale = MathF.Pow(2f, MathF.Ceiling(MathF.Log2(Math.Clamp(screenScale, 0.125f, 8f))));
        if (_shapes.TryGetValue((s.Id, scale), out var hit)) return hit;
        var r = ShapeRaster.Render(s, scale, _a.Bitmap);
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32, null, r.Bgra, r.Width * 4);
        bmp.Freeze();
        var v = (bmp, r);
        _shapes[(s.Id, scale)] = v;
        return v;
    }

    /// <summary>Farbtransformation (ohne reines Alpha, das macht PushOpacity) auf eine gerasterte Shape anwenden.</summary>
    public BitmapSource Tinted(VmfModel.Shape s, float screenScale, float[] mul, float[] add)
    {
        var (bmp, r) = Shape(s, screenScale);
        int key = HashCode.Combine((int)(mul[0] * 255), (int)(mul[1] * 255), (int)(mul[2] * 255), (int)(add[0] * 255), (int)(add[1] * 255), (int)(add[2] * 255), (int)(add[3] * 255));
        if (_tinted.TryGetValue((s.Id, r.Scale, key), out var t)) return t;
        var px = (byte[])r.Bgra.Clone();
        for (int i = 0; i < px.Length; i += 4)
        {
            float a = px[i + 3] / 255f; if (a <= 0f) continue;
            float b = px[i] / 255f / a, g = px[i + 1] / 255f / a, rr = px[i + 2] / 255f / a;
            rr = Math.Clamp(rr * mul[0] + add[0], 0f, 1f); g = Math.Clamp(g * mul[1] + add[1], 0f, 1f); b = Math.Clamp(b * mul[2] + add[2], 0f, 1f);
            float na = Math.Clamp(a + add[3], 0f, 1f);
            px[i] = (byte)(b * na * 255); px[i + 1] = (byte)(g * na * 255); px[i + 2] = (byte)(rr * na * 255); px[i + 3] = (byte)(na * 255);
        }
        t = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32, null, px, r.Width * 4);
        t.Freeze();
        _tinted[(s.Id, r.Scale, key)] = t;
        return t;
    }

    /// <summary>Font-Textur als Maske (Alpha = Helligkeit, weiß auf schwarz im Spiel).</summary>
    public BitmapSource FontMask(VmfModel.Font f)
    {
        if (_fonts.TryGetValue(f.Id, out var m)) return m;
        var img = _a.FontImage(f);
        if (img != null)
        {
            var px = new byte[img.Width * img.Height * 4];
            for (int i = 0, o = 0; i < px.Length; i += 4, o += 4)
            {
                byte l = Math.Max(img.Rgba[o], Math.Max(img.Rgba[o + 1], img.Rgba[o + 2]));
                px[i] = px[i + 1] = px[i + 2] = l; px[i + 3] = l;
            }
            m = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Pbgra32, null, px, img.Width * 4);
            m.Freeze();
        }
        _fonts[f.Id] = m;
        return m;
    }
}
