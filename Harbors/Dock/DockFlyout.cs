using System.Numerics;
using Harbor.Composition;
using Vortice.DirectWrite;
using Windows.UI.Composition;
using WColor = Windows.UI.Color;

namespace Harbor.Dock;

/// <summary>
/// What every panel that grows out of a dock icon has in common: the glass, the way it opens and closes from its icon, and
/// staying open while the pointer crosses to it. They live in the dock's composition tree, so they share its glass and
/// their animations run on the system compositor. Hit testing is done by the panel, in window pixels.
/// </summary>
internal abstract class DockFlyout
{
    protected const float WidthDip = 304;

    protected readonly record struct Box(float X, float Y, float W, float H)
    {
        public bool Contains(float x, float y) => x >= X && x < X + W && y >= Y && y < Y + H;
    }

    protected readonly CompositionHost _host;
    protected readonly ContainerVisual _root;
    protected Compositor C => _host.Compositor;
    protected CompositionEasingFunction _easeOut = null!;
    CompositionDrawingSurface? _shadow;

    protected DockTheme? _theme;
    protected float _s, _w, _h, _pad, _left, _top, _anchorX, _bottom;
    protected bool _built;
    int _closeToken;

    public bool IsOpen { get; private set; }
    public abstract bool IsPressed { get; }

    protected DockFlyout(CompositionHost host, ContainerVisual parent)
    {
        _host = host;
        _root = C.CreateContainerVisual();
        _root.IsVisible = false;
        parent.Children.InsertAtTop(_root);
    }

    public void Invalidate() => _built = false;

    public abstract void OnMove(float x, float y);
    public abstract void OnDown(float x, float y);
    public abstract void OnUp(float x, float y);
    public abstract void OnWheel(float x, float y, int direction);
    /// <summary>Called about once a second while open.</summary>
    public abstract void Refresh();

    // ------------------------------------------------------------------ building blocks

    protected SpriteVisual Rounded(CompositionBrush brush, Vector2 size, float radius)
    {
        var v = C.CreateSpriteVisual();
        v.Brush = brush;
        v.Size = size;
        var g = C.CreateRoundedRectangleGeometry();
        g.Size = size;
        g.CornerRadius = new Vector2(radius);
        v.Clip = C.CreateGeometricClip(g);
        return v;
    }

    /// <summary>Draws text into a new surface of exactly the size it needs.</summary>
    protected (CompositionDrawingSurface surface, Vector2 size) DrawText(string text, float px, FontWeight weight, WColor color, string? family = null)
    {
        using var format = _host.DWrite.CreateTextFormat(family ?? UiFonts.Text, null, weight, FontStyle.Normal, FontStretch.Normal, px, "en-us");
        format.WordWrapping = WordWrapping.NoWrap;
        using var layout = _host.DWrite.CreateTextLayout(text, format, 2000, 400);
        var m = layout.Metrics;
        int w = (int)MathF.Ceiling(m.WidthIncludingTrailingWhitespace) + 2, h = (int)MathF.Ceiling(m.Height) + 1;
        var surface = _host.CreateSurface(w, h);
        _host.Draw(surface, dc =>
        {
            dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            using var brush = dc.CreateSolidColorBrush(SurfacePainter.C4(color));
            dc.DrawTextLayout(new Vector2(1, 0), layout, brush, Vortice.Direct2D1.DrawTextOptions.None);
        });
        return (surface, new Vector2(w, h));
    }

    /// <summary>Draws text into a fresh surface on <paramref name="v"/> and sizes the sprite to fit.</summary>
    protected void SetText(SpriteVisual v, string text, float px, FontWeight weight, WColor color, string? family = null)
    {
        var (surface, size) = DrawText(text, px, weight, color, family);
        if (v.Brush is CompositionSurfaceBrush old) { (old.Surface as IDisposable)?.Dispose(); old.Dispose(); }
        v.Brush = C.CreateSurfaceBrush(surface);
        v.Size = size;
    }

    protected SpriteVisual AddText(string text, float px, FontWeight weight, WColor color, string? family = null)
    {
        var v = C.CreateSpriteVisual();
        SetText(v, text, px, weight, color, family);
        _root.Children.InsertAtTop(v);
        return v;
    }

    protected void AddDivider(DockTheme t, float s, float y)
    {
        var divider = C.CreateSpriteVisual();
        divider.Brush = C.CreateColorBrush(t.PanelDivider);
        divider.Size = new Vector2(_w - _pad * 2, MathF.Max(1, MathF.Round(s)));
        divider.Offset = new Vector3(_pad, y, 0);
        _root.Children.InsertAtTop(divider);
    }

    /// <summary>Starts a build: clears the panel, works out its size and lays down the shadow, glass and edge.</summary>
    protected void BuildChrome(DockTheme t, float scale, float heightDip)
    {
        _root.Children.RemoveAll();
        _theme = t;
        _s = scale;
        _w = MathF.Round(WidthDip * scale);
        _h = MathF.Round(heightDip * scale);
        _pad = MathF.Round(16 * scale);
        _root.Size = new Vector2(_w, _h);
        float radius = MathF.Round(16 * scale);

        _easeOut = C.CreateCubicBezierEasingFunction(new Vector2(0.23f, 1f), new Vector2(0.32f, 1f));

        // Soft shadow, same pre-blurred nine-grid technique as the bar.
        float e = MathF.Round(26 * scale);
        int side = (int)MathF.Ceiling((e + radius) * 2 + 4);
        _shadow?.Dispose();
        _shadow = _host.CreateSurface(side, side);
        _host.Draw(_shadow, dc => SurfacePainter.DrawShadow(dc, side, e, radius));
        var shadowBrush = C.CreateNineGridBrush();
        shadowBrush.Source = C.CreateSurfaceBrush(_shadow);
        shadowBrush.SetInsets(e + radius);
        var shadow = C.CreateSpriteVisual();
        shadow.Brush = shadowBrush;
        shadow.Size = new Vector2(_w + e * 2, _h + e * 2);
        shadow.Offset = new Vector3(-e, -e + 6 * scale, 0);
        shadow.Opacity = t.ShadowOpacity;
        _root.Children.InsertAtTop(shadow);

        // Glass: the live blurred desktop plus a stronger tint than the bar, because text sits on it.
        var glass = C.CreateContainerVisual();
        glass.Size = _root.Size;
        var clip = C.CreateRoundedRectangleGeometry();
        clip.Size = _root.Size;
        clip.CornerRadius = new Vector2(radius);
        glass.Clip = C.CreateGeometricClip(clip);
        var backdrop = C.CreateSpriteVisual();
        backdrop.RelativeSizeAdjustment = Vector2.One;
        backdrop.Brush = C.CreateHostBackdropBrush();
        glass.Children.InsertAtTop(backdrop);
        var tint = C.CreateSpriteVisual();
        tint.RelativeSizeAdjustment = Vector2.One;
        var tintBrush = C.CreateLinearGradientBrush();
        tintBrush.StartPoint = new Vector2(0, 0);
        tintBrush.EndPoint = new Vector2(0, 1);
        tintBrush.ColorStops.Add(C.CreateColorGradientStop(0, t.PanelTop));
        tintBrush.ColorStops.Add(C.CreateColorGradientStop(1, t.PanelBottom));
        tint.Brush = tintBrush;
        glass.Children.InsertAtTop(tint);
        _root.Children.InsertAtTop(glass);

        // Hairline edge with light catching the top, matching the dock.
        float stroke = MathF.Max(1, MathF.Round(scale));
        var shapes = C.CreateShapeVisual();
        shapes.Size = _root.Size;
        var edge = C.CreateRoundedRectangleGeometry();
        edge.Offset = new Vector2(stroke / 2);
        edge.Size = _root.Size - new Vector2(stroke);
        edge.CornerRadius = new Vector2(radius - stroke / 2);
        var edgeBrush = C.CreateLinearGradientBrush();
        edgeBrush.StartPoint = new Vector2(0, 0);
        edgeBrush.EndPoint = new Vector2(0, 1);
        edgeBrush.ColorStops.Add(C.CreateColorGradientStop(0, t.StrokeTop));
        edgeBrush.ColorStops.Add(C.CreateColorGradientStop(1, t.StrokeBottom));
        var edgeShape = C.CreateSpriteShape(edge);
        edgeShape.StrokeBrush = edgeBrush;
        edgeShape.StrokeThickness = stroke;
        shapes.Shapes.Add(edgeShape);
        _root.Children.InsertAtTop(shapes);
        _built = true;
    }

    // ------------------------------------------------------------------ open / close

    /// <summary>Shows the panel, already built, growing out of the icon at <paramref name="anchorX"/>.</summary>
    protected void Present(float anchorX, float bottom, float windowWidth)
    {
        _anchorX = anchorX;
        _bottom = bottom;
        float margin = 8 * _s;
        _left = Math.Clamp(anchorX - _w / 2, margin, MathF.Max(margin, windowWidth - _w - margin));
        Place();

        _closeToken++;
        if (!_root.IsVisible)
        {
            _root.Scale = new Vector3(0.95f, 0.95f, 1);
            _root.Opacity = 0;
            _root.IsVisible = true;
        }
        IsOpen = true;

        var scale = C.CreateVector3KeyFrameAnimation();
        scale.InsertExpressionKeyFrame(0, "this.StartingValue");
        scale.InsertKeyFrame(1, Vector3.One, _easeOut);
        scale.Duration = TimeSpan.FromMilliseconds(220);
        _root.StartAnimation("Scale", scale);
        var fade = C.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, 1f, _easeOut);
        fade.Duration = TimeSpan.FromMilliseconds(150);
        _root.StartAnimation("Opacity", fade);
    }

    /// <summary>Stands the panel on its bottom edge, which is what stays put if it changes height while open.</summary>
    protected void Place()
    {
        _top = _bottom - _h;
        _root.StopAnimation("Offset");
        _root.Offset = new Vector3(_left, _top, 0);
        // Grow out of the icon, not the panel's own centre.
        _root.CenterPoint = new Vector3(_anchorX - _left, _h, 0);
    }

    /// <summary>Exits faster than it enters: the system responding, not the user deciding.</summary>
    public virtual void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        int token = ++_closeToken;
        var batch = C.CreateScopedBatch(CompositionBatchTypes.Animation);
        var scale = C.CreateVector3KeyFrameAnimation();
        scale.InsertExpressionKeyFrame(0, "this.StartingValue");
        scale.InsertKeyFrame(1, new Vector3(0.97f, 0.97f, 1), _easeOut);
        scale.Duration = TimeSpan.FromMilliseconds(120);
        _root.StartAnimation("Scale", scale);
        var fade = C.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, 0f, _easeOut);
        fade.Duration = TimeSpan.FromMilliseconds(120);
        _root.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => { if (token == _closeToken) _root.IsVisible = false; };
    }

    /// <summary>The dock grew or shrank: stay just above it.</summary>
    public void SetBottom(float bottom)
    {
        if (!IsOpen) return;
        _bottom = bottom;
        float top = bottom - _h;
        if (top == _top) return;
        _top = top;
        var a = C.CreateSpringVector3Animation();
        a.FinalValue = new Vector3(_left, top, 0);
        a.Period = TimeSpan.FromMilliseconds(55);
        a.DampingRatio = 0.9f;
        _root.StartAnimation("Offset", a);
    }

    // ------------------------------------------------------------------ input (window pixels)

    public bool Contains(float x, float y) => IsOpen && x >= _left && x < _left + _w && y >= _top && y < _top + _h;

    /// <summary>The panel plus some slack, and the gap down to the icon, so crossing it doesn't count as leaving.</summary>
    public bool InKeepZone(float x, float y, float iconHalfWidth, float iconBottom)
    {
        if (!IsOpen) return false;
        float slack = 14 * _s;
        if (x >= _left - slack && x < _left + _w + slack && y >= _top - slack && y < _top + _h + slack) return true;
        return x >= _anchorX - iconHalfWidth && x <= _anchorX + iconHalfWidth && y >= _top + _h && y <= iconBottom;
    }

    protected void Fade(Visual v, float to, double ms = 120)
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.InsertExpressionKeyFrame(0, "this.StartingValue");
        a.InsertKeyFrame(1, to, _easeOut);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
    }

    protected void ScaleTo(Visual v, float to, double ms)
    {
        var a = C.CreateVector3KeyFrameAnimation();
        a.InsertExpressionKeyFrame(0, "this.StartingValue");
        a.InsertKeyFrame(1, new Vector3(to, to, 1), _easeOut);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Scale", a);
    }
}
