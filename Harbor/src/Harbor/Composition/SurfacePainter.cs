using System.Numerics;
using Harbor.Interop;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;
using WColor = Windows.UI.Color;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace Harbor.Composition;

/// <summary>Direct2D drawing routines for the dock's composition surfaces.</summary>
internal static unsafe class SurfacePainter
{
    public static Color4 C4(WColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    static Color4 C4(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    public static void DrawImage(ID2D1DeviceContext dc, IconImage img, RawRectF dest)
    {
        var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied));
        fixed (byte* p = img.Pixels)
        {
            using var bmp = dc.CreateBitmap(new SizeI(img.Width, img.Height), (IntPtr)p, (uint)(img.Width * 4), props);
            dc.DrawBitmap(bmp, dest, 1f, InterpolationMode.HighQualityCubic, null, null);
        }
    }

    /// <summary>Fallback tile for apps without a usable icon: a soft gradient squircle with the first letter.</summary>
    public static void DrawLetterTile(ID2D1DeviceContext dc, string name, float size)
    {
        int hash = 0;
        foreach (char ch in name) hash = hash * 31 + ch;
        float hue = (hash & 0xFFFF) / 65535f;
        var top = Hsv(hue, 0.55f, 0.95f);
        var bottom = Hsv((hue + 0.08f) % 1f, 0.75f, 0.70f);
        float inset = size * 0.08f;
        var rect = new RawRectF(inset, inset, size - inset, size - inset);
        using (var stops = dc.CreateGradientStopCollection([new GradientStop(0, top), new GradientStop(1, bottom)]))
        using (var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, inset), new Vector2(0, size - inset)), stops))
            dc.FillRoundedRectangle(new RoundedRectangle(rect, size * 0.22f, size * 0.22f), brush);

        string letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString().ToUpperInvariant();
        using var format = CompositionHost.Instance.DWrite.CreateTextFormat(UiFonts.Display, null,
            FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, size * 0.46f, "en-us");
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        using var white = dc.CreateSolidColorBrush(C4(255, 255, 255, 240));
        dc.DrawText(letter, format, new Rect(0, 0, size, size), white);
    }

    /// <summary>The Start tile: a glassy indigo squircle with a 2x2 grid glyph.</summary>
    public static void DrawStartTile(ID2D1DeviceContext dc, float size)
    {
        float inset = size * 0.08f;
        var rect = new RawRectF(inset, inset, size - inset, size - inset);
        float r = size * 0.23f;
        using (var stops = dc.CreateGradientStopCollection([
                   new GradientStop(0, C4(118, 140, 255)), new GradientStop(0.55f, C4(84, 92, 236)), new GradientStop(1, C4(58, 48, 190))]))
        using (var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(inset, inset), new Vector2(size - inset, size - inset)), stops))
            dc.FillRoundedRectangle(new RoundedRectangle(rect, r, r), brush);

        // Top sheen.
        using (var stops = dc.CreateGradientStopCollection([new GradientStop(0, C4(255, 255, 255, 70)), new GradientStop(1, C4(255, 255, 255, 0))]))
        using (var sheen = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, inset), new Vector2(0, size * 0.55f)), stops))
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(inset, inset, size - inset, size * 0.55f), r, r), sheen);

        float g = size * 0.135f, gap = size * 0.05f, cx = size / 2, cy = size / 2;
        using var white = dc.CreateSolidColorBrush(C4(255, 255, 255, 245));
        for (int i = 0; i < 4; i++)
        {
            float x = i % 2 == 0 ? cx - gap / 2 - g : cx + gap / 2;
            float y = i < 2 ? cy - gap / 2 - g : cy + gap / 2;
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + g, y + g), g * 0.28f, g * 0.28f), white);
        }
    }

    /// <summary>The app's own icon: an indigo squircle with a dock whose middle tile is magnified.</summary>
    /// <summary>The time over the date, with no tile behind them, sitting straight on the dock's glass.</summary>
    public static void DrawClock(ID2D1DeviceContext dc, float width, float height, string time, string date, WColor timeColor, WColor dateColor)
    {
        dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        var factory = CompositionHost.Instance.DWrite;

        (IDWriteTextLayout layout, float h) Fit(string text, float px, FontWeight weight)
        {
            for (; ; )
            {
                using var format = factory.CreateTextFormat(UiFonts.Text, null, weight, FontStyle.Normal, FontStretch.Normal, px, "en-us");
                format.WordWrapping = WordWrapping.NoWrap;
                format.TextAlignment = TextAlignment.Center;
                var layout = factory.CreateTextLayout(text, format, width, height);
                var m = layout.Metrics;
                // Some languages write longer dates than the tile was sized for.
                if (m.WidthIncludingTrailingWhitespace <= width || px < 6) return (layout, m.Height);
                layout.Dispose();
                px *= 0.92f;
            }
        }

        var (timeLayout, timeHeight) = Fit(time, height * 0.40f, FontWeight.SemiBold);
        var (dateLayout, dateHeight) = Fit(date, height * 0.28f, FontWeight.Normal);
        using (timeLayout)
        using (dateLayout)
        using (var timeBrush = dc.CreateSolidColorBrush(C4(timeColor)))
        using (var dateBrush = dc.CreateSolidColorBrush(C4(dateColor)))
        {
            float top = (height - timeHeight - dateHeight) / 2;
            dc.DrawTextLayout(new Vector2(0, top), timeLayout, timeBrush, DrawTextOptions.None);
            dc.DrawTextLayout(new Vector2(0, top + timeHeight), dateLayout, dateBrush, DrawTextOptions.None);
        }
    }

    /// <summary>A coloured tile with a ring that empties as the time runs out, and a dot in the middle while it's running.</summary>
    public static void DrawPomodoroTile(ID2D1DeviceContext dc, float size, float fraction, bool running, bool onBreak)
    {
        float inset = size * 0.08f, w = size - inset * 2, r = w * 0.24f;
        var rect = new RawRectF(inset, inset, size - inset, size - inset);
        var top = onBreak ? C4(70, 205, 150) : C4(255, 112, 92);
        var bottom = onBreak ? C4(20, 140, 110) : C4(208, 44, 62);
        using (var stops = dc.CreateGradientStopCollection([new GradientStop(0, top), new GradientStop(1, bottom)]))
        using (var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(inset, inset), new Vector2(size - inset, size - inset)), stops))
            dc.FillRoundedRectangle(new RoundedRectangle(rect, r, r), brush);

        var centre = new Vector2(size / 2);
        float radius = size * 0.235f, thickness = MathF.Max(1.5f, size * 0.08f);
        using (var track = dc.CreateSolidColorBrush(C4(255, 255, 255, 70)))
            dc.DrawEllipse(new Ellipse(centre, radius, radius), track, thickness);
        using var white = dc.CreateSolidColorBrush(C4(255, 255, 255, 250));
        // Clockwise from twelve, drawn as a run of dots close enough to read as a solid line with round ends.
        float sweep = MathF.Tau * Math.Clamp(fraction, 0, 1);
        int steps = (int)MathF.Ceiling(sweep * radius / (thickness * 0.3f));
        for (int i = 0; i <= steps && sweep > 0; i++)
        {
            float a = -MathF.PI / 2 + sweep * i / steps;
            dc.FillEllipse(new Ellipse(centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius, thickness / 2, thickness / 2), white);
        }
        if (running) dc.FillEllipse(new Ellipse(centre, thickness * 0.75f, thickness * 0.75f), white);
    }

    public static void DrawHarborTile(ID2D1DeviceContext dc, float size)
    {
        float inset = size * 0.08f, w = size - inset * 2, r = w * 0.24f;
        var rect = new RawRectF(inset, inset, size - inset, size - inset);
        using (var stops = dc.CreateGradientStopCollection([new GradientStop(0, C4(120, 142, 255)), new GradientStop(1, C4(52, 40, 176))]))
        using (var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(inset, inset), new Vector2(size - inset, size - inset)), stops))
            dc.FillRoundedRectangle(new RoundedRectangle(rect, r, r), brush);
        using (var stops = dc.CreateGradientStopCollection([new GradientStop(0, C4(255, 255, 255, 70)), new GradientStop(1, C4(255, 255, 255, 0))]))
        using (var sheen = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, inset), new Vector2(0, size * 0.6f)), stops))
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(inset, inset, size - inset, size * 0.6f), r, r), sheen);

        float shelfH = MathF.Max(1.5f, size * 0.07f), shelfW = size * 0.58f, shelfY = size * 0.69f;
        using (var soft = dc.CreateSolidColorBrush(C4(255, 255, 255, 110)))
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF((size - shelfW) / 2, shelfY, (size + shelfW) / 2, shelfY + shelfH), shelfH / 2, shelfH / 2), soft);

        float t = size * 0.125f, big = size * 0.21f, gap = size * 0.042f, baseY = shelfY - size * 0.03f;
        float x = (size - (t + gap + big + gap + t)) / 2;
        using var white = dc.CreateSolidColorBrush(C4(255, 255, 255, 245));
        foreach (float side in new[] { t, big, t })
        {
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, baseY - side, x + side, baseY), side * 0.28f, side * 0.28f), white);
            x += side + gap;
        }
    }

    /// <summary>Now Playing tile: rounded album art (or a note on a gradient), dimmed with a play glyph while paused.</summary>
    public static void DrawMediaTile(ID2D1DeviceContext dc, float size, IconImage? art, bool playing)
    {
        float inset = size * 0.08f, r = size * 0.22f;
        var rect = new RawRectF(inset, inset, size - inset, size - inset);
        var rounded = new RoundedRectangle(rect, r, r);
        if (art != null)
        {
            var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied));
            fixed (byte* p = art.Pixels)
            {
                using var bmp = dc.CreateBitmap(new SizeI(art.Width, art.Height), (IntPtr)p, (uint)(art.Width * 4), props);
                using var brush = dc.CreateBitmapBrush(bmp, null, null);
                // Centre-crop the art into the tile.
                float side = Math.Min(art.Width, art.Height);
                float k = (size - inset * 2) / side;
                brush.Transform = Matrix3x2.CreateTranslation(-(art.Width - side) / 2, -(art.Height - side) / 2) * Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(inset, inset);
                brush.InterpolationMode = BitmapInterpolationMode.Linear;
                dc.FillRoundedRectangle(rounded, brush);
            }
        }
        else
        {
            using var stops = dc.CreateGradientStopCollection([new GradientStop(0, C4(255, 98, 122)), new GradientStop(1, C4(150, 70, 230))]);
            using var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(inset, inset), new Vector2(size - inset, size - inset)), stops);
            dc.FillRoundedRectangle(rounded, brush);
            DrawGlyph(dc, "\uEC4F", size, size * 0.42f, C4(255, 255, 255, 235));
        }
        using (var edge = dc.CreateSolidColorBrush(C4(255, 255, 255, 40)))
            dc.DrawRoundedRectangle(rounded, edge, Math.Max(1, size / 96f));
        if (!playing)
        {
            using var dim = dc.CreateSolidColorBrush(C4(0, 0, 0, 90));
            dc.FillRoundedRectangle(rounded, dim);
            DrawGlyph(dc, "\uE768", size, size * 0.34f, C4(255, 255, 255, 245));
        }
    }

    static void DrawGlyph(ID2D1DeviceContext dc, string glyph, float size, float fontSize, Color4 color)
    {
        using var format = CompositionHost.Instance.DWrite.CreateTextFormat(UiFonts.Icons, null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, fontSize, "en-us");
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        using var brush = dc.CreateSolidColorBrush(color);
        dc.DrawText(glyph, format, new Rect(0, 0, size, size), brush);
    }

    public static void DrawCircle(ID2D1DeviceContext dc, float size, WColor color)
    {
        using var brush = dc.CreateSolidColorBrush(C4(color));
        dc.FillEllipse(new Ellipse(new Vector2(size / 2, size / 2), size / 2, size / 2), brush);
    }

    /// <summary>Soft rounded-rect shadow for a nine-grid brush. Returns nothing; the caller knows the insets.</summary>
    public static void DrawShadow(ID2D1DeviceContext dc, float side, float extent, float radius)
    {
        // Record the unblurred shape on a private context; the surface context is mid-draw.
        using var scratch = CompositionHost.Instance.CreateDeviceContext();
        using var list = scratch.CreateCommandList();
        scratch.Target = list;
        scratch.BeginDraw();
        using (var black = scratch.CreateSolidColorBrush(C4(0, 0, 0, 255)))
            scratch.FillRoundedRectangle(new RoundedRectangle(new RawRectF(extent, extent, side - extent, side - extent), radius, radius), black);
        scratch.EndDraw();
        list.Close();
        scratch.Target = null;

        using var blur = new Vortice.Direct2D1.Effects.GaussianBlur(dc)
        {
            StandardDeviation = extent / 3f,
            BorderMode = BorderMode.Soft,
            Optimization = GaussianBlurOptimization.Quality,
        };
        blur.SetInput(0, list, true);
        dc.DrawImage(blur.Output, null, null, InterpolationMode.Linear, CompositeMode.SourceOver);
    }

    public static (IDWriteTextLayout layout, float width, float height) MeasureLabel(string text, float fontSize)
    {
        using var format = CompositionHost.Instance.DWrite.CreateTextFormat(UiFonts.Text, null,
            FontWeight.Medium, FontStyle.Normal, FontStretch.Normal, fontSize, "en-us");
        format.WordWrapping = WordWrapping.NoWrap;
        var layout = CompositionHost.Instance.DWrite.CreateTextLayout(text, format, 2000, 200);
        var m = layout.Metrics;
        return (layout, m.WidthIncludingTrailingWhitespace, m.Height);
    }

    public static void DrawLabel(ID2D1DeviceContext dc, IDWriteTextLayout layout, float w, float h, float padX, float padY,
        WColor background, WColor border, WColor text)
    {
        float r = Math.Min(h / 2, (h + padY * 2) * 0.32f);
        var rect = new RawRectF(0.5f, 0.5f, w + padX * 2 - 0.5f, h + padY * 2 - 0.5f);
        using (var bg = dc.CreateSolidColorBrush(C4(background)))
            dc.FillRoundedRectangle(new RoundedRectangle(rect, r, r), bg);
        using (var stroke = dc.CreateSolidColorBrush(C4(border)))
            dc.DrawRoundedRectangle(new RoundedRectangle(rect, r, r), stroke, 1f);
        using var fg = dc.CreateSolidColorBrush(C4(text));
        dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        dc.DrawTextLayout(new Vector2(padX, padY), layout, fg, DrawTextOptions.EnableColorFont);
    }

    static Color4 Hsv(float h, float s, float v)
    {
        float r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1);
        float g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1);
        float b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        return new Color4(((r - 1) * s + 1) * v, ((g - 1) * s + 1) * v, ((b - 1) * s + 1) * v, 1);
    }
}
