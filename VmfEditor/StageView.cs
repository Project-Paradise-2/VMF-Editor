using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VmfCore;

namespace VmfEditor;

/// <summary>
/// Bühne: zeichnet die Anzeigeliste der aktuellen Zeitleiste (Shapes pixelgleich gerastert, Textfelder mit dem Textur-Font,
/// Masken als Rechteck-Ausschnitt), Auswahl per Klick (oberstes Element der aktuellen Zeitleiste, Alt = tiefstes),
/// Doppelklick betritt ein Sprite, Ziehen verschiebt, Pfeiltasten um 1 px (Umschalt 10 px). Mausrad = Zoom, mittlere Taste = schieben.
/// </summary>
public sealed class StageView : FrameworkElement
{
    public BankAssets Assets;
    public ImageCache Images;
    public List<SceneItem> Items = new();
    public SceneItem Selected;
    /// <summary>Ursprung der aktuellen Zeitleiste auf der Bühne (Hauptfilm 0,0; Sprite: Bühnenmitte).</summary>
    public Matrix2D Origin = Matrix2D.Identity;
    public double Zoom = 0.6;
    public Vector Pan = new(20, 20);
    public bool ShowVariables = true, ShowBounds, ShowAllBounds;

    public event Action<SceneItem> SelectionChanged;
    public event Action<SceneItem> EnterRequested;
    /// <summary>Verschieben beendet: Element, alte Matrix (für Rückgängig).</summary>
    public event Action<SceneItem, Matrix2D> MoveCommitted;

    static readonly Brush StageBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12));
    static readonly Brush BackBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x27, 0x2B));
    static readonly Pen StagePen = new(new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x5E)), 1);
    static readonly Pen WidePen = new(new SolidColorBrush(Color.FromArgb(0x70, 0x4A, 0x9E, 0xFF)), 1) { DashStyle = DashStyles.Dash };
    static readonly Pen SelPen = new(new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF)), 1.5);
    static readonly Pen BoundsPen = new(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xC8, 0x40)), 1);
    static readonly Brush HandleBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF));
    static readonly Typeface Mono = new("Consolas");

    static StageView()
    {
        foreach (var f in new Freezable[] { StageBrush, BackBrush, StagePen, WidePen, SelPen, BoundsPen, HandleBrush }) f.Freeze();
    }

    public StageView() { Focusable = true; ClipToBounds = true; }

    Matrix StageToScreen => new(Zoom, 0, 0, Zoom, Pan.X, Pan.Y);

    public void FitStage()
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;
        Zoom = Math.Min((ActualWidth - 40) / 1280.0, (ActualHeight - 40) / 960.0);
        Pan = new Vector((ActualWidth - 1280 * Zoom) / 2, (ActualHeight - 960 * Zoom) / 2);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ Zeichnen
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new MatrixTransform(StageToScreen));
        dc.DrawRectangle(StageBrush, StagePen, new Rect(0, 0, 1280, 960));
        dc.DrawLine(WidePen, new Point(0, 120), new Point(1280, 120));   // sichtbarer Bereich bei 16:9
        dc.DrawLine(WidePen, new Point(0, 840), new Point(1280, 840));
        if (Assets != null) DrawList(dc, Items, Origin, CxForm.IdentityF, 0);
        dc.Pop();
        if (Selected != null && Assets != null) DrawSelection(dc, Selected);
    }

    void DrawList(DrawingContext dc, List<SceneItem> items, Matrix2D world, (float[] mul, float[] add) cx, int level)
    {
        if (items == null || level > 30) return;
        int maskUntil = -1;
        foreach (var it in items)
        {
            if (maskUntil >= 0 && it.Depth > maskUntil) { dc.Pop(); maskUntil = -1; }
            if (it.Hidden) continue;
            var w = world * it.Matrix;
            if (it.ClipDepth > 0)
            {
                // Maske: Ausschnitt = Begrenzung der Maskenform (Rechteck, wie der Vorfilter im Spiel)
                var b = Scene.LocalBounds(Assets.Model, it);
                if (maskUntil >= 0) dc.Pop();
                var r = w.Transform(b);
                dc.PushClip(new RectangleGeometry(new Rect(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H))));
                maskUntil = it.ClipDepth;
                continue;
            }
            var c = CxForm.Combine(cx, it.Cx);
            if (c.mul[3] + c.add[3] <= 0.003f) continue;
            DrawItem(dc, it, w, c, level);
            if (ShowAllBounds) { var b = Scene.LocalBounds(Assets.Model, it); if (!b.IsEmpty) DrawQuad(dc, w, b, BoundsPen); }
        }
        if (maskUntil >= 0) dc.Pop();
    }

    void DrawItem(DrawingContext dc, SceneItem it, Matrix2D w, (float[] mul, float[] add) c, int level)
    {
        switch (it.Char)
        {
            case VmfModel.Shape s:
                {
                    float screen = MathF.Sqrt(MathF.Abs(w.Det)) * (float)Zoom;
                    var (bmp, r) = Images.Shape(s, screen);
                    bool tint = MathF.Abs(c.mul[0] - 1) > 0.01f || MathF.Abs(c.mul[1] - 1) > 0.01f || MathF.Abs(c.mul[2] - 1) > 0.01f
                                || MathF.Abs(c.add[0]) > 0.004f || MathF.Abs(c.add[1]) > 0.004f || MathF.Abs(c.add[2]) > 0.004f || MathF.Abs(c.add[3]) > 0.004f;
                    var img = tint ? Images.Tinted(s, screen, c.mul, c.add) : bmp;
                    dc.PushTransform(new MatrixTransform(ToWpf(w)));
                    dc.PushOpacity(Math.Clamp(c.mul[3], 0f, 1f));
                    dc.DrawImage(img, new Rect(r.OriginX, r.OriginY, r.Width / r.Scale, r.Height / r.Scale));
                    dc.Pop(); dc.Pop();
                    break;
                }
            case VmfModel.EditText e:
                DrawText(dc, e, w, c);
                break;
            default:
                if (it.Children != null) DrawList(dc, it.Children, w, c, level + 1);
                break;
        }
    }

    void DrawText(DrawingContext dc, VmfModel.EditText e, Matrix2D w, (float[] mul, float[] add) c)
    {
        string text = e.Text;
        bool placeholder = false;
        if (string.IsNullOrEmpty(text) && ShowVariables && !string.IsNullOrEmpty(e.Variable)) { text = "‹" + e.Variable + "›"; placeholder = true; }
        if (string.IsNullOrEmpty(text)) return;
        Assets.Model.Fonts.TryGetValue(e.FontId, out var font);
        var mask = font != null ? Images.FontMask(font) : null;
        dc.PushTransform(new MatrixTransform(ToWpf(w)));
        if (mask == null)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), Math.Max(4, e.Height * 0.9),
                ColorBrush(e.Color, c, placeholder), 1.0);
            ft.MaxTextWidth = Math.Max(10, e.Bounds.W);
            dc.DrawText(ft, new Point(e.Bounds.X + 2, e.Bounds.Y + 1));
        }
        else
        {
            foreach (var q in TextLayout.Layout(e, font, text))
            {
                var g = q.Glyph;
                var brush = new ImageBrush(mask) { Viewbox = new Rect(g.U0, g.V0, g.U1 - g.U0, g.V1 - g.V0), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
                var rect = new Rect(q.Dest.X, q.Dest.Y, Math.Max(0, q.Dest.W), Math.Max(0, q.Dest.H));
                dc.PushOpacityMask(brush);
                dc.DrawRectangle(ColorBrush(q.Color ?? e.Color, c, placeholder), null, rect);
                dc.Pop();
            }
        }
        dc.Pop();
    }

    static Brush ColorBrush(Rgba col, (float[] mul, float[] add) c, bool placeholder)
    {
        float r = Math.Clamp(col.R / 255f * c.mul[0] + c.add[0], 0, 1), g = Math.Clamp(col.G / 255f * c.mul[1] + c.add[1], 0, 1),
              b = Math.Clamp(col.B / 255f * c.mul[2] + c.add[2], 0, 1), a = Math.Clamp(col.A / 255f * c.mul[3] + c.add[3], 0, 1);
        if (placeholder) { r = 0.55f; g = 0.75f; b = 1f; a = Math.Max(0.35f, a * 0.7f); }
        var br = new SolidColorBrush(Color.FromArgb((byte)(a * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
        br.Freeze();
        return br;
    }

    static Matrix ToWpf(Matrix2D m) => new(m.A, m.B, m.C, m.D, m.Tx, m.Ty);

    void DrawQuad(DrawingContext dc, Matrix2D w, RectF b, Pen pen)
    {
        var p0 = w.Apply(new Vec2(b.X, b.Y)); var p1 = w.Apply(new Vec2(b.XMax, b.Y)); var p2 = w.Apply(new Vec2(b.XMax, b.YMax)); var p3 = w.Apply(new Vec2(b.X, b.YMax));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(p0.X, p0.Y), false, true);
            ctx.LineTo(new Point(p1.X, p1.Y), true, false); ctx.LineTo(new Point(p2.X, p2.Y), true, false); ctx.LineTo(new Point(p3.X, p3.Y), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    void DrawSelection(DrawingContext dc, SceneItem it)
    {
        var b = Scene.LocalBounds(Assets.Model, it);
        var w = Origin * Scene.WorldOf(it);
        var screen = new Matrix2D { A = (float)Zoom, D = (float)Zoom, Tx = (float)Pan.X, Ty = (float)Pan.Y } * w;
        if (b.IsEmpty) b = new RectF(-4, -4, 8, 8);
        DrawQuad(dc, screen, b, SelPen);
        foreach (var p in new[] { new Vec2(b.X, b.Y), new Vec2(b.XMax, b.Y), new Vec2(b.XMax, b.YMax), new Vec2(b.X, b.YMax) })
        {
            var q = screen.Apply(p);
            dc.DrawRectangle(HandleBrush, null, new Rect(q.X - 3, q.Y - 3, 6, 6));
        }
        var o = screen.Apply(new Vec2(0, 0));   // Registrierpunkt
        dc.DrawLine(SelPen, new Point(o.X - 6, o.Y), new Point(o.X + 6, o.Y));
        dc.DrawLine(SelPen, new Point(o.X, o.Y - 6), new Point(o.X, o.Y + 6));
    }

    // ------------------------------------------------------------------ Auswahl / Maus
    Point _down; bool _dragging, _panning; Matrix2D _dragStart; Vec2 _dragStageStart;

    Vec2 ToStage(Point p) => new((float)((p.X - Pan.X) / Zoom), (float)((p.Y - Pan.Y) / Zoom));

    /// <summary>Oberstes Element der aktuellen Zeitleiste unter dem Punkt (deep = tiefstes Kind).</summary>
    public SceneItem HitTest(Point screen, bool deep)
    {
        if (Assets == null) return null;
        var p = ToStage(screen);
        return Hit(Items, Origin, p, deep);
    }

    SceneItem Hit(List<SceneItem> items, Matrix2D world, Vec2 p, bool deep)
    {
        if (items == null) return null;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            if (it.ClipDepth > 0 || it.Hidden) continue;
            if (it.Cx.Ma + it.Cx.Aa <= 0) continue;
            var w = world * it.Matrix;
            var b = Scene.LocalBounds(Assets.Model, it);
            if (b.IsEmpty) continue;
            var local = w.Inverse().Apply(p);
            if (!b.Contains(local)) continue;
            if (deep && it.Children != null) { var d = Hit(it.Children, w, p, true); if (d != null) return d; }
            return it;
        }
        return null;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _down = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        { _panning = true; CaptureMouse(); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        var hit = HitTest(_down, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (e.ClickCount == 2 && hit != null && hit.Char is VmfModel.Sprite) { EnterRequested?.Invoke(hit); return; }
        if (hit != Selected) { Selected = hit; SelectionChanged?.Invoke(hit); InvalidateVisual(); }
        if (Selected != null && Selected.Owner != null && Selected.MatrixOp != null)
        {
            _dragging = true; _dragStart = Selected.Matrix; _dragStageStart = ToStage(_down);
            CaptureMouse();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panning) { Pan += p - _down; _down = p; InvalidateVisual(); return; }
        if (!_dragging || Selected == null) return;
        var d = ToStage(p) - _dragStageStart;
        // Verschiebung in den Koordinaten der Eltern (Bühne bzw. Sprite)
        var parentWorld = Origin;
        for (var q = Selected.Parent; q != null; q = q.Parent) parentWorld = parentWorld * q.Matrix;
        var inv = parentWorld.Inverse();
        var dl = inv.Apply(d) - inv.Apply(new Vec2(0, 0));
        if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) < 3 && Selected.Matrix.Tx == _dragStart.Tx && Selected.Matrix.Ty == _dragStart.Ty) return;
        var m = _dragStart; m.Tx = MathF.Round((m.Tx + dl.X) * 20) / 20; m.Ty = MathF.Round((m.Ty + dl.Y) * 20) / 20;
        Selected.Matrix = m;
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panning) { _panning = false; ReleaseMouseCapture(); return; }
        if (!_dragging) return;
        _dragging = false; ReleaseMouseCapture();
        if (Selected != null && (Selected.Matrix.Tx != _dragStart.Tx || Selected.Matrix.Ty != _dragStart.Ty)) MoveCommitted?.Invoke(Selected, _dragStart);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        double old = Zoom;
        Zoom = Math.Clamp(Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.05, 16);
        Pan = new Vector(p.X - (p.X - Pan.X) * Zoom / old, p.Y - (p.Y - Pan.Y) * Zoom / old);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Selected == null || Selected.Owner == null || Selected.MatrixOp == null) return;
        float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        float dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return;
        var old = Selected.Matrix;
        var m = old; m.Tx += dx; m.Ty += dy;
        Selected.Matrix = m;
        MoveCommitted?.Invoke(Selected, old);
        e.Handled = true;
    }
}
