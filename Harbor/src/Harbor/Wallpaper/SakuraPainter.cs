using System.Numerics;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using DriverType = Vortice.Direct3D.DriverType;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Harbor.Wallpaper;

/// <summary>The pictures a scene paints once and then animates, ready for the shader.</summary>
internal sealed class BakedLayers : IDisposable
{
    readonly List<IDisposable> _owned = new();
    public ID3D11ShaderResourceView[] Views { get; }

    public BakedLayers(params (ID3D11Texture2D texture, ID3D11ShaderResourceView view)[] layers)
    {
        Views = layers.Select(l => l.view).ToArray();
        foreach (var (texture, view) in layers) { _owned.Add(view); _owned.Add(texture); }
    }

    public void Dispose()
    {
        foreach (var o in _owned) o.Dispose();
        _owned.Clear();
    }
}

/// <summary>
/// Paints the cherry trees for the Sakura scene: real branching limbs carrying thousands of individual five-petal blossoms.
/// That much detail is far too expensive to work out per pixel on every frame, so it's drawn once with Direct2D, and the
/// shader only moves the result and adds the water and the falling petals. Works in a fixed 1600x1000 space and scales to fit.
/// </summary>
internal sealed class SakuraPainter : IDisposable
{
    const float RefW = 1600, RefH = 1000;
    const int Variants = 8;

    readonly ID3D11Device _device;
    readonly ID3D11Device _painting;
    readonly ID3D11DeviceContext _paintingContext;
    readonly ID2D1Factory1 _factory;
    readonly ID2D1Device _d2dDevice;
    readonly ID2D1DeviceContext _dc;
    readonly ID2D1StrokeStyle _round;
    readonly Random _r = new(20260405);
    float _scale;

    // Blossoms in three kinds of light, each drawn at several angles so no two neighbours match.
    ID2D1Bitmap1[] _lit = null!, _mid = null!, _shade = null!;
    ID2D1Bitmap1 _puffDeep = null!, _puffPale = null!;
    float _spritePx;

    SakuraPainter(ID3D11Device device)
    {
        _device = device;
        // Painted in software and only the finished pictures go to the graphics chip. Painting with the chip directly
        // kept about 300 MB of driver memory in Harbor for as long as it ran, for something that happens once.
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? painting, out ID3D11DeviceContext? context).CheckError();
        _painting = painting!;
        _paintingContext = context!;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        using var dxgi = _painting.QueryInterface<IDXGIDevice>();
        _d2dDevice = _factory.CreateDevice(dxgi);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dc.SetDpi(96, 96);
        _round = _factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
    }

    public void Dispose()
    {
        foreach (var set in new[] { _lit, _mid, _shade })
            if (set != null) foreach (var b in set) b.Dispose();
        _puffDeep?.Dispose();
        _puffPale?.Dispose();
        _round.Dispose();
        _dc.Dispose();
        _d2dDevice.Dispose();
        _factory.Dispose();
        _paintingContext.ClearState();
        _paintingContext.Flush();
        _paintingContext.Dispose();
        _painting.Dispose();
    }

    /// <summary>Paints the three layers, sized for a picture <paramref name="width"/> wide, as textures on <paramref name="device"/>.</summary>
    public static BakedLayers Bake(ID3D11Device device, int width)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var painter = new SakuraPainter(device);
        var layers = painter.PaintAll(Math.Clamp(width, 800, 2560));
        Log.Info($"Painted the sakura trees at {width} wide in {clock.ElapsedMilliseconds} ms");
        return layers;
    }

    BakedLayers PaintAll(int width)
    {
        _scale = width / RefW;
        MakeSprites();
        var far = Layer((int)(width * 0.5f), PaintFar, blur: 1.4f);
        var tree = Layer(width, PaintTrees, blur: 0);
        var near = Layer((int)(width * 0.4f), PaintNear, blur: 6.5f);
        return new BakedLayers(far, tree, near);
    }

    float R() => (float)_r.NextDouble();
    float R(float a, float b) => a + (b - a) * R();

    static readonly PixelFormat Pf = new(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);

    ID2D1Bitmap1 Offscreen(int w, int h) =>
        _dc.CreateBitmap(new SizeI(Math.Max(1, w), Math.Max(1, h)), IntPtr.Zero, 0, new BitmapProperties1(Pf, 96, 96, BitmapOptions.Target));

    (ID3D11Texture2D, ID3D11ShaderResourceView) Layer(int width, Action paint, float blur)
    {
        int height = (int)(width / 1.6f);
        using var canvas = _painting.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        using var surface = canvas.QueryInterface<IDXGISurface>();
        using var final = _dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(Pf, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));

        float k = width / RefW;
        void Draw(ID2D1Image target)
        {
            _dc.Target = target;
            _dc.BeginDraw();
            _dc.Clear(new Color4(0, 0, 0, 0));
            _dc.Transform = Matrix3x2.CreateScale(k);
            paint();
            _dc.Transform = Matrix3x2.Identity;
            _dc.EndDraw().CheckError();
        }

        if (blur <= 0)
        {
            Draw(final);
        }
        else
        {
            using var sharp = Offscreen(width, height);
            Draw(sharp);
            _dc.Target = final;
            _dc.BeginDraw();
            _dc.Clear(new Color4(0, 0, 0, 0));
            using var effect = new GaussianBlur(_dc) { StandardDeviation = blur * k, BorderMode = BorderMode.Soft };
            effect.SetInput(0, sharp, true);
            _dc.DrawImage(effect.Output, null, null, InterpolationMode.Linear, CompositeMode.SourceOver);
            _dc.EndDraw().CheckError();
        }
        _dc.Target = null;

        using var readable = _painting.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        _paintingContext.CopyResource(readable, canvas);
        var map = _paintingContext.Map(readable, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var texture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None),
                new SubresourceData(map.DataPointer, map.RowPitch));
            return (texture, _device.CreateShaderResourceView(texture, null));
        }
        finally { _paintingContext.Unmap(readable, 0); }
    }

    // ------------------------------------------------------------------ sprites

    static Color4 C(float r, float g, float b, float a = 1) => new(r * a, g * a, b * a, a);

    void MakeSprites()
    {
        _spritePx = MathF.Ceiling(40 * _scale);
        _lit = Blossoms(C(0.97f, 0.60f, 0.70f), C(1.00f, 0.84f, 0.88f), C(1.00f, 0.95f, 0.96f));
        _mid = Blossoms(C(0.92f, 0.52f, 0.65f), C(0.98f, 0.72f, 0.80f), C(0.99f, 0.85f, 0.89f));
        _shade = Blossoms(C(0.74f, 0.36f, 0.50f), C(0.84f, 0.52f, 0.63f), C(0.90f, 0.63f, 0.72f));
        _puffDeep = Puff(0.84f, 0.52f, 0.63f);
        _puffPale = Puff(0.98f, 0.76f, 0.82f);
    }

    ID2D1Bitmap1 Puff(float r, float g, float b)
    {
        int px = (int)(64 * _scale) + 8;
        var bmp = Offscreen(px, px);
        _dc.Target = bmp;
        _dc.BeginDraw();
        _dc.Clear(new Color4(0, 0, 0, 0));
        using (var stops = _dc.CreateGradientStopCollection([new GradientStop(0, C(r, g, b, 0.95f)), new GradientStop(0.55f, C(r, g, b, 0.62f)), new GradientStop(1, C(r, g, b, 0))]))
        using (var brush = _dc.CreateRadialGradientBrush(new RadialGradientBrushProperties { Center = new Vector2(px / 2f), RadiusX = px / 2f, RadiusY = px / 2f }, stops))
            _dc.FillRectangle(new Rect(0, 0, px, px), brush);
        _dc.EndDraw().CheckError();
        _dc.Target = null;
        return bmp;
    }

    ID2D1Bitmap1[] Blossoms(Color4 heart, Color4 body, Color4 edge)
    {
        var sprites = new ID2D1Bitmap1[Variants];
        int px = (int)_spritePx;
        float c = px / 2f, radius = px * 0.48f;
        using var stops = _dc.CreateGradientStopCollection([new GradientStop(0, heart), new GradientStop(0.3f, body), new GradientStop(1, edge)]);
        using var brush = _dc.CreateRadialGradientBrush(new RadialGradientBrushProperties { Center = Vector2.Zero, RadiusX = radius, RadiusY = radius }, stops);
        using var crease = _dc.CreateSolidColorBrush(new Color4(heart.R, heart.G, heart.B, 0.35f));
        using var stamen = _dc.CreateSolidColorBrush(C(0.80f, 0.34f, 0.48f, 0.7f));
        using var pollen = _dc.CreateSolidColorBrush(C(1.0f, 0.86f, 0.56f, 0.85f));

        using var petal = _factory.CreatePathGeometry();
        using (var sink = petal.Open())
        {
            // One petal pointing right: narrow at the heart, broad and notched at the tip, as cherry petals are.
            float r = radius;
            sink.BeginFigure(Vector2.Zero, FigureBegin.Filled);
            sink.AddBezier(new BezierSegment { Point1 = new(r * 0.30f, -r * 0.42f), Point2 = new(r * 0.86f, -r * 0.52f), Point3 = new(r * 0.98f, -r * 0.13f) });
            sink.AddLine(new Vector2(r * 0.84f, 0));
            sink.AddLine(new Vector2(r * 0.98f, r * 0.13f));
            sink.AddBezier(new BezierSegment { Point1 = new(r * 0.86f, r * 0.52f), Point2 = new(r * 0.30f, r * 0.42f), Point3 = Vector2.Zero });
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        for (int v = 0; v < Variants; v++)
        {
            sprites[v] = Offscreen(px, px);
            _dc.Target = sprites[v];
            _dc.BeginDraw();
            _dc.Clear(new Color4(0, 0, 0, 0));
            float spin = v * 0.73f;
            // Most blossoms face us; some are turned away, which squashes them.
            float squash = v % 3 == 2 ? 0.62f : v % 3 == 1 ? 0.84f : 1f;
            var place = Matrix3x2.CreateRotation(v * 1.1f) * Matrix3x2.CreateScale(1, squash) * Matrix3x2.CreateRotation(spin) * Matrix3x2.CreateTranslation(c, c);
            for (int i = 0; i < 5; i++)
            {
                _dc.Transform = Matrix3x2.CreateRotation(i * MathF.Tau / 5 + (i % 2) * 0.05f) * place;
                _dc.FillGeometry(petal, brush);
                _dc.DrawLine(new Vector2(radius * 0.18f, 0), new Vector2(radius * 0.7f, 0), crease, MathF.Max(0.5f, radius * 0.035f), _round);
            }
            _dc.Transform = place;
            float dot = MathF.Max(0.6f, radius * 0.045f);
            for (int s = 0; s < 9; s++)
            {
                float a = s * 0.698f, len = radius * (0.26f + (s % 3) * 0.05f);
                var tip = new Vector2(MathF.Cos(a), MathF.Sin(a)) * len;
                _dc.DrawLine(Vector2.Zero, tip, stamen, dot * 0.6f, _round);
                _dc.FillEllipse(new Ellipse(tip, dot, dot), pollen);
            }
            _dc.Transform = Matrix3x2.Identity;
            _dc.EndDraw().CheckError();
            _dc.Target = null;
        }
        return sprites;
    }

    void Stamp(ID2D1Bitmap1 sprite, Vector2 centre, float size, float opacity = 1f)
    {
        float h = size / 2;
        _dc.DrawBitmap(sprite, new RawRectF(centre.X - h, centre.Y - h, centre.X + h, centre.Y + h), opacity, InterpolationMode.Linear, null, null);
    }

    // ------------------------------------------------------------------ trees

    readonly record struct Seg(Vector2 A, Vector2 B, float WA, float WB, int Depth);
    readonly record struct Tip(Vector2 At, float Weight);

    sealed class Tree
    {
        public readonly List<Seg> Segs = new();
        public readonly List<Tip> Tips = new();
        public float Floor = 1000, MinX = -200, MaxX = 1800;
    }

    /// <summary>Grows a limb in short steps, so it bends like wood. Thin ends arch over and droop, as a cherry's do.</summary>
    void Limb(Tree tree, Vector2 p, float angle, float length, float width, int depth)
    {
        int steps = Math.Max(4, (int)(length / 20));
        float step = length / steps, w = width;
        float taper = MathF.Pow(depth == 0 ? 0.25f : 0.62f, 1f / steps);
        float curve = (R() - 0.5f) * 0.11f;
        float droop = depth switch { 0 => 0.055f, 1 => 0.034f, 2 => 0.016f, _ => 0.0f };
        for (int i = 0; i < steps; i++)
        {
            angle += curve + (R() - 0.5f) * 0.26f;
            angle += MathF.Cos(angle) >= 0 ? droop : -droop;
            // Keep the crown over the bank and out of the lake and the view.
            float tooLow = Math.Clamp((p.Y - tree.Floor) / 70f, 0, 1) * 0.34f;
            angle += MathF.Cos(angle) >= 0 ? -tooLow : tooLow;
            if (p.X > tree.MaxX) angle = TurnToward(angle, MathF.PI + 0.9f, 0.16f);
            if (p.X < tree.MinX) angle = TurnToward(angle, -0.9f, 0.16f);
            var next = p + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * step;
            // Anything that strays too far is simply where this limb ends.
            if (depth <= 2 && (next.Y > tree.Floor + 40 || next.X > tree.MaxX + 50 || next.X < tree.MinX - 50)) return;
            tree.Segs.Add(new Seg(p, next, w, w * taper, depth));
            p = next;
            w *= taper;
            if (depth <= 2 && i > steps / 4 && R() < 0.34f)
                Limb(tree, p, angle + (R() < 0.5f ? -1 : 1) * R(0.5f, 1.15f), length * R(0.32f, 0.58f), MathF.Max(1.2f, w * 0.6f), 0);
            if (depth <= 1 && R() < (depth == 0 ? 0.92f : 0.42f))
                tree.Tips.Add(new Tip(p, depth == 0 ? 1f : 0.75f));
        }
        if (depth <= 0) { tree.Tips.Add(new Tip(p, 1.2f)); return; }
        int children = R() < 0.4f ? 3 : 2;
        for (int c = 0; c < children; c++)
        {
            float side = children == 3 ? (c - 1) : (c == 0 ? -0.7f : 0.7f);
            Limb(tree, p, angle + side * R(0.32f, 0.72f), length * R(0.62f, 0.86f), w * R(0.68f, 0.86f), depth - 1);
        }
    }

    static float TurnToward(float angle, float target, float by)
    {
        float difference = MathF.IEEERemainder(target - angle, MathF.Tau);
        return angle + Math.Clamp(difference, -by, by);
    }

    Tree GrowTree(Vector2 root, float trunkWidth, float reach, float lean, float floor, float minX, float maxX, (float angle, float amount)[] limbs)
    {
        var tree = new Tree { Floor = floor, MinX = minX, MaxX = maxX };
        var p = root;
        float w = trunkWidth, angle = -MathF.PI / 2 + lean;
        const int steps = 16;
        float step = reach * 0.5f / steps;

        // Roots reach out into the ground around the foot of the trunk.
        for (int i = 0; i < 5; i++)
        {
            float a = i switch { 0 => 2.75f, 1 => 2.3f, 2 => 1.6f, 3 => 0.85f, _ => 0.35f };
            var tree2 = root + new Vector2(0, -trunkWidth * 0.25f);
            Limb(tree, tree2, a + R(-0.12f, 0.12f), trunkWidth * R(1.0f, 1.7f), trunkWidth * 0.34f, 0);
        }
        tree.Tips.Clear();

        // The first limbs leave the trunk part of the way up, at different heights, rather than from one point.
        int limb = 0, firstLimb = steps - limbs.Length * 2;
        for (int i = 0; i < steps; i++)
        {
            angle += (R() - 0.5f) * 0.12f;
            var next = p + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * step;
            float flare = 1f + 0.45f * MathF.Pow(1f - i / (float)steps, 4);
            float taper = i < firstLimb ? 0.985f : 0.93f;
            tree.Segs.Add(new Seg(p, next, w * flare, w * taper, 6));
            p = next;
            w *= taper;
            if (i >= firstLimb && (i - firstLimb) % 2 == 1 && limb < limbs.Length)
            {
                var (limbAngle, amount) = limbs[limb++];
                Limb(tree, p, limbAngle + (R() - 0.5f) * 0.1f, reach * amount, MathF.Max(w * 0.72f, trunkWidth * 0.3f), 4);
            }
        }
        while (limb < limbs.Length)
        {
            var (limbAngle, amount) = limbs[limb++];
            Limb(tree, p, limbAngle, reach * amount, MathF.Max(w * 0.8f, trunkWidth * 0.3f), 4);
        }
        return tree;
    }

    void DrawWood(Tree tree, float minDepthForBark)
    {
        using var dark = _dc.CreateSolidColorBrush(C(0.13f, 0.09f, 0.10f));
        using var mid = _dc.CreateSolidColorBrush(C(0.27f, 0.20f, 0.20f));
        using var lit = _dc.CreateSolidColorBrush(C(0.46f, 0.36f, 0.33f));
        using var band = _dc.CreateSolidColorBrush(C(0.12f, 0.08f, 0.09f, 0.3f));
        foreach (var s in tree.Segs) _dc.DrawLine(s.A, s.B, dark, (s.WA + s.WB) / 2, _round);
        var toLight = Vector2.Normalize(new Vector2(0.55f, -0.85f));
        foreach (var s in tree.Segs)
        {
            float w = (s.WA + s.WB) / 2;
            if (w < 2.2f) continue;
            var along = Vector2.Normalize(s.B - s.A);
            var normal = new Vector2(-along.Y, along.X);
            if (Vector2.Dot(normal, toLight) < 0) normal = -normal;
            _dc.DrawLine(s.A + normal * w * 0.14f, s.B + normal * w * 0.14f, mid, w * 0.58f, _round);
            _dc.DrawLine(s.A + normal * w * 0.27f, s.B + normal * w * 0.27f, lit, w * 0.2f, _round);
            // Cherry bark is marked by fine bands running around the trunk.
            if (s.Depth < minDepthForBark || w < 7) continue;
            float length = Vector2.Distance(s.A, s.B);
            for (float d = R(0, 8); d < length; d += R(5f, 15f))
            {
                var at = s.A + along * d;
                float reach = w * R(0.08f, 0.3f), shift = R(-0.4f, 0.4f) * w;
                _dc.DrawLine(at + normal * (shift - reach), at + normal * (shift + reach), band, R(0.7f, 1.5f), _round);
            }
        }
    }

    /// <summary>Clusters are drawn from the bottom of the crown up, in shade first and then in light, which is what gives it depth.</summary>
    void DrawBlossoms(Tree tree, float size, float density)
    {
        var tips = tree.Tips.OrderByDescending(t => t.At.Y + R(-34, 34)).ToList();
        float top = tips.Min(t => t.At.Y), bottom = tips.Max(t => t.At.Y), span = MathF.Max(1, bottom - top);

        foreach (var t in tips)
        {
            float spread = size * 1.9f * t.Weight;
            Stamp(_puffDeep, t.At + new Vector2(R(-6, 6), size * 0.3f), spread * R(1.5f, 2.1f), R(0.3f, 0.48f));
        }
        foreach (var t in tips)
        {
            float height = 1f - (t.At.Y - top) / span;
            float spread = size * 1.5f * t.Weight;
            Stamp(_puffPale, t.At + new Vector2(R(-5, 5), R(-5, 5)), spread * R(1.8f, 2.5f), R(0.26f, 0.46f) * (0.6f + height * 0.6f));

            int count = (int)(R(11, 21) * density * t.Weight);
            for (int i = 0; i < count; i++)
            {
                float a = R() * MathF.Tau, d = spread * MathF.Sqrt(R()) * 1.05f;
                var at = t.At + new Vector2(MathF.Cos(a) * d, MathF.Sin(a) * d * 0.82f);
                // Blossoms on the underside of a cluster, facing away from the sky, are in shade.
                float underside = Math.Clamp((at.Y - t.At.Y) / spread * 0.5f + 0.5f, 0, 1);
                float light = height * 0.42f + (1 - underside) * 0.58f + R(-0.16f, 0.16f);
                var set = light > 0.60f ? _lit : light > 0.34f ? _mid : _shade;
                Stamp(set[_r.Next(Variants)], at, size * R(0.76f, 1.18f), R(0.86f, 1f));
            }
        }
        // A last scatter of the brightest ones catches the sun on top of everything else.
        foreach (var t in tips)
        {
            if (R() > 0.55f) continue;
            float a = R() * MathF.Tau, d = size * R(0.3f, 1.4f);
            Stamp(_lit[_r.Next(Variants)], t.At + new Vector2(MathF.Cos(a) * d, MathF.Sin(a) * d - size * 0.5f), size * R(0.86f, 1.22f));
        }
    }

    // ------------------------------------------------------------------ the three layers

    ID2D1PathGeometry Land(params Vector2[] outline)
    {
        var path = _factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(outline[0], FigureBegin.Filled);
        for (int i = 1; i < outline.Length; i++) sink.AddLine(outline[i]);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return path;
    }

    Vector2[] Ridge(float baseY, float amplitude, float bottom)
    {
        var points = new List<Vector2> { new(-20, bottom) };
        float phase1 = R() * 9, phase2 = R() * 9;
        for (float x = -20; x <= RefW + 20; x += 12)
        {
            float y = baseY + MathF.Sin(x * 0.0041f + phase1) * amplitude + MathF.Sin(x * 0.0113f + phase2) * amplitude * 0.42f + (R() - 0.5f) * amplitude * 0.06f;
            points.Add(new Vector2(x, y));
        }
        points.Add(new Vector2(RefW + 20, bottom));
        return points.ToArray();
    }

    void PaintFar()
    {
        const float shore = 628;
        using (var path = Land(Ridge(486, 34, shore + 2)))
        using (var brush = _dc.CreateSolidColorBrush(C(0.68f, 0.74f, 0.90f)))
            _dc.FillGeometry(path, brush);
        using (var path = Land(Ridge(548, 22, shore + 2)))
        using (var brush = _dc.CreateSolidColorBrush(C(0.60f, 0.68f, 0.76f)))
            _dc.FillGeometry(path, brush);
        using (var path = Land(Ridge(590, 9, shore + 2)))
        using (var brush = _dc.CreateSolidColorBrush(C(0.56f, 0.62f, 0.60f)))
            _dc.FillGeometry(path, brush);

        // A distant shoreline of trees, mostly in bloom with a few evergreens among them.
        for (int i = 0; i < 620; i++)
        {
            float x = R(-30, RefW + 30), y = shore - R(6, 62), radius = R(11, 34) * (0.7f + (y - 560) / 110f);
            Stamp(R() < 0.14f ? _puffDeep : _puffPale, new Vector2(x, y), radius * 2.4f, R(0.7f, 1f));
        }
        for (int i = 0; i < 420; i++)
            Stamp(_puffPale, new Vector2(R(-30, RefW + 30), shore - R(14, 58)), R(16, 40), R(0.5f, 0.9f));
        using (var dark = _dc.CreateSolidColorBrush(C(0.44f, 0.46f, 0.42f)))
            _dc.FillRectangle(new Rect(-20, shore - 3, RefW + 40, 4), dark);
    }

    void PaintTrees()
    {
        PaintBanks();

        var right = GrowTree(new Vector2(1492, 930), 42, 460, -0.04f, 440, 1190, 1800,
        [
            (-2.7f, 0.66f), (-2.3f, 0.72f), (-1.85f, 0.70f), (-1.3f, 0.56f), (-0.75f, 0.42f),
        ]);
        DrawWood(right, 5);
        DrawBlossoms(right, 11.5f, 1.15f);
        Grass(new Vector2(1492, 932), 60);

        var left = GrowTree(new Vector2(214, 892), 74, 640, 0.08f, 450, -200, 760,
        [
            (-2.6f, 0.46f), (-2.05f, 0.58f), (-1.5f, 0.62f), (-1.05f, 0.72f), (-0.6f, 0.76f), (-0.3f, 0.62f),
        ]);
        DrawWood(left, 5);
        DrawBlossoms(left, 14f, 1.15f);
        Grass(new Vector2(214, 894), 100);
    }

    /// <summary>Blades over the foot of a trunk, so it stands in the grass rather than on it.</summary>
    void Grass(Vector2 at, float halfWidth)
    {
        using var blade = _dc.CreateSolidColorBrush(C(0.36f, 0.50f, 0.24f));
        using var lit = _dc.CreateSolidColorBrush(C(0.62f, 0.74f, 0.38f));
        for (int i = 0; i < 520; i++)
        {
            var from = at + new Vector2(R(-halfWidth, halfWidth), R(-2, 18));
            _dc.DrawLine(from, from + new Vector2(R(-4, 4), -R(7, 17)), R() < 0.3f ? lit : blade, R(0.9f, 1.7f), _round);
        }
    }

    void PaintBanks()
    {
        using var leftBank = Land(new(-30, 806), new(90, 802), new(240, 812), new(400, 842), new(560, 892), new(700, 950), new(790, 1030), new(-30, 1030));
        using var rightBank = Land(new(1660, 866), new(1540, 868), new(1420, 884), new(1320, 926), new(1250, 980), new(1215, 1030), new(1660, 1030));
        using (var stops = _dc.CreateGradientStopCollection([new GradientStop(0, C(0.47f, 0.60f, 0.30f)), new GradientStop(1, C(0.21f, 0.33f, 0.18f))]))
        using (var brush = _dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(0, 800), EndPoint = new Vector2(0, 1030) }, stops))
        {
            _dc.FillGeometry(leftBank, brush);
            _dc.FillGeometry(rightBank, brush);
        }
        // Wet earth where the land meets the water.
        using (var wet = _dc.CreateSolidColorBrush(C(0.13f, 0.17f, 0.13f, 0.75f)))
        {
            _dc.DrawGeometry(leftBank, wet, 5);
            _dc.DrawGeometry(rightBank, wet, 5);
        }

        using var blade = _dc.CreateSolidColorBrush(C(0.33f, 0.47f, 0.22f, 0.8f));
        using var bladeLit = _dc.CreateSolidColorBrush(C(0.60f, 0.72f, 0.36f, 0.8f));
        for (int i = 0; i < 9000; i++)
        {
            bool left = R() < 0.7f;
            float x = left ? R(-20, 780) : R(1220, 1620);
            float edge = left ? LeftEdge(x) : RightEdge(x);
            float y = R(edge, 1010);
            float length = R(5, 13) * (0.7f + (y - 800) / 300f);
            var tip = new Vector2(x + R(-3.5f, 3.5f), y - length);
            _dc.DrawLine(new Vector2(x, y), tip, R() < 0.3f ? bladeLit : blade, R(0.8f, 1.6f), _round);
        }

        // The ground under a cherry tree in full bloom is carpeted with what it has dropped.
        for (int i = 0; i < 7000; i++)
        {
            bool left = R() < 0.76f;
            float cx = left ? 240 : 1480, cy = left ? 905 : 950;
            float x = cx + Gauss() * (left ? 300 : 190), y = cy + Gauss() * 60;
            float edge = left ? LeftEdge(x) : RightEdge(x);
            if (y < edge + 2 || (left && x > 790) || (!left && x < 1215)) continue;
            float size = R(5, 10) * (0.8f + (y - 800) / 260f);
            var set = R() < 0.55f ? _lit : R() < 0.7f ? _mid : _shade;
            float half = size / 2;
            _dc.DrawBitmap(set[_r.Next(Variants)], new RawRectF(x - half, y - half * 0.5f, x + half, y + half * 0.5f), R(0.75f, 1f), InterpolationMode.Linear, null, null);
        }
    }

    static float LeftEdge(float x) => x < 240 ? 806 + x * 0.025f : 812 + MathF.Pow((x - 240) / 550f, 1.4f) * 215f;
    static float RightEdge(float x) => x > 1420 ? 884 - (x - 1420) * 0.075f : 884 + MathF.Pow((1420 - x) / 205f, 1.5f) * 145f;

    float Gauss() => MathF.Sqrt(-2 * MathF.Log(MathF.Max(1e-5f, R()))) * MathF.Cos(MathF.Tau * R());

    void PaintNear()
    {
        var branch = new Tree();
        Limb(branch, new Vector2(1640, -14), 2.72f, 170, 8, 1);
        DrawWood(branch, 9);
        DrawBlossoms(branch, 30f, 0.42f);
    }
}
