using System.Numerics;
using System.Runtime.InteropServices;
using Harbor.Composition;
using Harbor.Interop;
using Harbor.Settings;
using Vortice;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WColor = Windows.UI.Color;

namespace Harbor.Dock;

internal enum DockButton { Left, Middle }

/// <summary>
/// The dock surface. A full-width, click-through, topmost window at the bottom of the primary monitor,
/// rendered entirely with Windows.UI.Composition. Magnification, reflow and bounces are expression and
/// spring animations evaluated by the system compositor, so they stay smooth at the display refresh
/// rate without any per-frame work on our UI thread.
/// </summary>
internal sealed class DockWindow : NativeWindow
{
    // Magnification is a smooth stretch of the dock's base axis: a raised-cosine scale field around the
    // cursor, and Disp(x), its closed-form integral, maps a base position to its magnified position.
    // Every slot occupies exactly the stretched image of its own base footprint, so icon sizes and
    // spacing always agree and the point under the cursor stays put while neighbours slide outward.
    static string T(string x) => $"Min(Abs({x} - p.MouseX) / p.R, 1)";
    static string Disp(string x) =>
        $"(p.Amount * p.Hover * p.R * ({T(x)} * 0.5 + Sin({T(x)} * 3.14159265) * 0.15915494) * ({x} < p.MouseX ? -1 : 1))";

    sealed class Slot
    {
        public required DockItem Item;
        public required CompositionPropertySet Props; // BaseX, Bounce, Appear, Press
        public required Visual Icon;
        public SpriteVisual? Dot;
        public CompositionColorBrush? DotColor;
        public CompositionRoundedRectangleGeometry? DotShape;
        public SpriteVisual? Label;
        public CompositionDrawingSurface? IconSurface;
        public int PaintedIconVersion = -1;
        public float PaintedSize;
        public string? LabelText;
        public float TargetX;
        public float Width;
        public bool Removing;
        public bool WasRunning, WasActive;
        public bool Bouncing;
        /// <summary>Just created: placed directly and popped in, rather than gliding from nowhere.</summary>
        public bool Fresh = true;
        /// <summary>How wide it is compared to an icon. The clock needs room for its text.</summary>
        public float Aspect = 1;
        public bool IsSeparator => Item.Kind == DockItemKind.Separator;
    }

    readonly CompositionHost _host = CompositionHost.Instance;
    Compositor C => _host.Compositor;
    readonly List<Slot> _slots = new();

    DesktopWindowTarget _target = null!;
    ContainerVisual _root = null!, _dockLayer = null!, _slotsLayer = null!, _labelsLayer = null!;
    ContainerVisual _bar = null!;
    SpriteVisual _barShadow = null!, _tint = null!;
    CompositionLinearGradientBrush _tintBrush = null!, _strokeBrush = null!;
    CompositionColorBrush _outlineBrush = null!;
    CompositionNineGridBrush _shadowBrush = null!;
    CompositionDrawingSurface _shadowSurface = null!;
    CompositionRoundedRectangleGeometry _clipGeometry = null!, _strokeGeometry = null!, _outlineGeometry = null!;
    CompositionPropertySet _p = null!;
    SpriteVisual _handle = null!;
    CompositionColorBrush _handleBrush = null!;
    CompositionRoundedRectangleGeometry _handleShape = null!;
    CompositionEasingFunction _easeOut = null!, _easeInOut = null!;

    DockSettings _settings;
    DockTheme _theme;
    Metrics _m = null!;
    IntPtr _monitor;
    int _winX, _winY;

    bool _hovering, _transparent, _hiddenByFullscreen, _autoHidden;
    // Auto-hide state machine (see UpdateAutoHide). Hysteresis is what keeps the dock from bouncing.
    bool _autoHideWanted;
    long _engagedUntil, _revealDwellStart;
    const int RevealDwellMs = 140, HideGraceMs = 700;
    int _monitorBottom;
    float _mouseX = -10000;
    Slot? _pressed, _hoverSlot, _labelSlot; // hovered icon, and the one whose label is showing
    Native.POINT _pressPoint;
    bool _dragging;
    DateTime _lastClick;

    public event Action<DockItem, DockButton>? ItemInvoked;
    public event Action<DockItem>? ItemPressed;
    public event Action<DockItem?, Native.POINT>? ContextMenuRequested;
    public event Action<int>? IconSizeWheel;
    public event Action<DockItem, int>? ItemReordered;
    public event Action<DockItem, int>? ItemScrolled;
    public event Action<DockItem>? ItemDraggedOff;
    /// <summary>The quick panel's Open Harbor button.</summary>
    public event Action? OpenAppRequested;
    public event Action<WallpaperMode>? WallpaperModeChosen;
    /// <summary>The display setup changed; the controller checks which screens there are now.</summary>
    public event Action? DisplayChanged;
    /// <summary>Asked each time the quick panel opens, because the dock doesn't own wallpaper settings.</summary>
    public Func<WallpaperMode>? CurrentWallpaperMode { get; set; }

    /// <summary>Which screen this dock is on, by a name that stays the same while screens come and go.</summary>
    public string DeviceName { get; }
    public double Scale => _m.Scale;

    public bool IsHovering => _hovering;
    public Native.RECT ScreenBounds => new(_winX, _winY, _winX + _m.WinW, _winY + _m.WinH);

    /// <summary>The bar's screen rectangle in its resting (unmagnified) state.</summary>
    public Native.RECT BarScreenRect => new(
        _winX + (int)_barLeft, _winY + (int)_m.BarTop, _winX + (int)_barRight, _winY + (int)(_m.BarTop + _m.BarH));

    float _barLeft, _barRight;

    public DockWindow(DockSettings settings, IntPtr monitor, string deviceName)
    {
        _settings = settings;
        _theme = DockTheme.Resolve(settings);
        _monitor = monitor;
        DeviceName = deviceName;
        var mi = Native.GetMonitorInfo(_monitor);

        Create("HarborDock",
            Native.WS_POPUP,
            Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP
            | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT,
            mi.rcMonitor.Left, mi.rcMonitor.Bottom - 200, mi.rcMonitor.Width, 200, IntPtr.Zero, "Harbor Dock");
        _transparent = true;

        Native.SetLayeredWindowAttributes(Handle, 0, 255, 0x2 /* LWA_ALPHA */);
        Native.SetDwmInt(Handle, Native.DWMWA_USE_HOSTBACKDROPBRUSH, 1);
        Native.SetDwmInt(Handle, Native.DWMWA_EXCLUDED_FROM_PEEK, 1);
        Native.SetDwmInt(Handle, Native.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        Native.SetDwmInt(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 1 /* DWMWCP_DONOTROUND */);

        s_docks.Add(this);
        ListenForPointer();

        // Raw input only reports physical mice; this slow poll covers touch, pen, RDP and injected moves.
        Native.SetTimer(Handle, (IntPtr)1, 100, IntPtr.Zero);

        BuildVisualTree();
        Relayout(animate: false);
        Native.ShowWindow(Handle, Native.SW_SHOWNA);
    }

    /// <summary>
    /// Raw input lets us see the cursor while the window is click-through, without a global hook. Windows only delivers
    /// it to one window in a process, so whichever dock registered last gets it and passes it on to the others.
    /// </summary>
    void ListenForPointer()
    {
        var rid = new[] { new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100 /* RIDEV_INPUTSINK */, hwndTarget = Handle } };
        if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            Log.Warn("RegisterRawInputDevices failed; dock hover falls back to polling");
    }

    // ------------------------------------------------------------------ metrics

    sealed class Metrics
    {
        public float Scale, W, Gap, PadX, PadTop, PadBottom, BarH, Radius, Margin, Amount, R;
        public float LabelGap, LabelFont, LabelPadX, LabelPadY, DotD, PillW, SepW, SepH, ShadowExtent, Stroke;
        public int WinW, WinH, IconPx;
        public float BarTop, BottomY, DotY;

        public static float IconPixels(DockSettings s, float scale, double fitted) =>
            MathF.Round((float)Math.Clamp(Math.Min(s.IconSize, fitted), DockLimits.MinIcon, DockLimits.MaxIcon) * scale);

        public static Metrics Compute(DockSettings s, float scale, int monitorWidth, int monitorHeight, double fitted)
        {
            var m = new Metrics { Scale = scale };
            m.W = IconPixels(s, scale, fitted);
            m.Gap = MathF.Round(m.W * 0.14f);
            m.PadX = MathF.Round(m.W * 0.2f);
            m.PadTop = MathF.Round(m.W * 0.15f);
            m.PadBottom = MathF.Round(m.W * 0.21f);
            m.BarH = m.W + m.PadTop + m.PadBottom;
            m.Radius = MathF.Min(m.BarH / 2, m.BarH * (float)Math.Clamp(s.CornerRoundness, 0.1, 0.5));
            m.Margin = MathF.Round(7 * scale);
            m.Amount = s.Magnification ? (float)Math.Clamp(s.MagnificationAmount, 0.05, 1.5) : 0f;
            m.R = m.W * 2.9f;
            m.LabelGap = MathF.Round(9 * scale);
            m.LabelFont = 12.5f * scale;
            m.LabelPadX = MathF.Round(10 * scale);
            m.LabelPadY = MathF.Round(4.5f * scale);
            m.DotD = MathF.Max(3, MathF.Round(4 * scale));
            m.PillW = MathF.Round(m.W * 0.3f);
            m.SepW = MathF.Max(1, MathF.Round(scale));
            m.SepH = MathF.Round(m.W * 0.62f);
            m.ShadowExtent = MathF.Round(22 * scale);
            m.Stroke = MathF.Max(1, MathF.Round(scale));
            m.IconPx = (int)MathF.Ceiling(m.W * (1 + MathF.Max(m.Amount, 0.15f)));

            // The window is one fixed height, tall enough for the largest dock with the quick panel on top.
            // Resizing the dock then never moves the window, which isn't synchronised with composition and
            // would show as jitter. It's click-through and mostly empty, so the extra height costs nothing.
            float labelH = m.LabelFont * 1.4f + m.LabelPadY * 2;
            float maxW = (float)DockLimits.MaxIcon * scale;
            float room = maxW * 1.36f + maxW * (float)DockLimits.MaxZoom + m.LabelGap + labelH + (QuickPanel.MaxHeightDip + 24) * scale;
            m.WinW = monitorWidth;
            m.WinH = (int)MathF.Ceiling(MathF.Min(m.Margin + room, monitorHeight));
            m.BarTop = m.WinH - m.Margin - m.BarH;
            m.BottomY = m.BarTop + m.PadTop + m.W;
            m.DotY = m.BottomY + m.PadBottom * 0.52f;
            return m;
        }
    }

    // ------------------------------------------------------------------ visual tree

    void BuildVisualTree()
    {
        _target = _host.CreateDesktopWindowTarget(Handle, topmost: true);
        _root = C.CreateContainerVisual();
        _target.Root = _root;

        _easeOut = C.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        _easeInOut = C.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));

        _p = C.CreatePropertySet();
        foreach (var name in new[] { "MouseX", "Hover", "Amount", "R", "W", "BottomY", "BarTop", "BarH", "DotY", "BarL", "BarR", "LabelGap", "Radius", "SepW" })
            _p.InsertScalar(name, 0);
        _p.InsertScalar("MouseX", -10000);

        _dockLayer = C.CreateContainerVisual();
        _dockLayer.RelativeSizeAdjustment = Vector2.One;
        _root.Children.InsertAtTop(_dockLayer);

        // Soft shadow under the bar (nine-grid of a pre-blurred rounded rect).
        _shadowBrush = C.CreateNineGridBrush();
        _barShadow = C.CreateSpriteVisual();
        _barShadow.Brush = _shadowBrush;
        _dockLayer.Children.InsertAtTop(_barShadow);

        // The bar: host backdrop (the live, blurred desktop behind us) + gradient tint, clipped to a rounded rect.
        _bar = C.CreateContainerVisual();
        _clipGeometry = C.CreateRoundedRectangleGeometry();
        _bar.Clip = C.CreateGeometricClip(_clipGeometry);
        var backdrop = C.CreateSpriteVisual();
        backdrop.RelativeSizeAdjustment = Vector2.One;
        backdrop.Brush = C.CreateHostBackdropBrush();
        _bar.Children.InsertAtTop(backdrop);
        _tint = C.CreateSpriteVisual();
        _tint.RelativeSizeAdjustment = Vector2.One;
        _tintBrush = C.CreateLinearGradientBrush();
        _tintBrush.StartPoint = new Vector2(0, 0);
        _tintBrush.EndPoint = new Vector2(0, 1);
        _tint.Brush = _tintBrush;
        _bar.Children.InsertAtTop(_tint);
        _dockLayer.Children.InsertAtTop(_bar);

        // Hairline outline plus an inner top highlight, like light catching the edge of glass.
        var strokes = C.CreateShapeVisual();
        strokes.RelativeSizeAdjustment = Vector2.One;
        _outlineGeometry = C.CreateRoundedRectangleGeometry();
        _outlineBrush = C.CreateColorBrush();
        var outline = C.CreateSpriteShape(_outlineGeometry);
        outline.StrokeBrush = _outlineBrush;
        strokes.Shapes.Add(outline);
        _strokeGeometry = C.CreateRoundedRectangleGeometry();
        _strokeBrush = C.CreateLinearGradientBrush();
        _strokeBrush.StartPoint = new Vector2(0, 0);
        _strokeBrush.EndPoint = new Vector2(0, 1);
        var highlight = C.CreateSpriteShape(_strokeGeometry);
        highlight.StrokeBrush = _strokeBrush;
        strokes.Shapes.Add(highlight);
        _dockLayer.Children.InsertAtTop(strokes);

        _slotsLayer = C.CreateContainerVisual();
        _slotsLayer.RelativeSizeAdjustment = Vector2.One;
        _dockLayer.Children.InsertAtTop(_slotsLayer);
        _labelsLayer = C.CreateContainerVisual();
        _labelsLayer.RelativeSizeAdjustment = Vector2.One;
        _dockLayer.Children.InsertAtTop(_labelsLayer);

        _panel = new QuickPanel(_host, _dockLayer);
        _panel.OpenApp += () => { ClosePanel(); OpenAppRequested?.Invoke(); };
        _panel.ModeChosen += m => WallpaperModeChosen?.Invoke(m);

        // Bar geometry follows the magnified edges.
        string left = $"(p.BarL + {Disp("p.BarL")})";
        string right = $"(p.BarR + {Disp("p.BarR")})";
        Bind(_bar, "Offset", $"Vector3({left}, p.BarTop, 0)");
        Bind(_bar, "Size", $"Vector2({right} - {left}, p.BarH)");
        Bind(_clipGeometry, "Size", "bar.Size", ("bar", _bar));
        Bind(_outlineGeometry, "Offset", "Vector2(bar.Offset.X + s.Half, bar.Offset.Y + s.Half)", ("bar", _bar));
        Bind(_outlineGeometry, "Size", "Vector2(bar.Size.X - s.Stroke, bar.Size.Y - s.Stroke)", ("bar", _bar));
        Bind(_strokeGeometry, "Offset", "Vector2(bar.Offset.X + s.Stroke * 1.5, bar.Offset.Y + s.Stroke * 1.5)", ("bar", _bar));
        Bind(_strokeGeometry, "Size", "Vector2(bar.Size.X - s.Stroke * 3, bar.Size.Y - s.Stroke * 3)", ("bar", _bar));
        Bind(_barShadow, "Offset", "Vector3(bar.Offset.X - s.Extent, bar.Offset.Y - s.Extent + s.Drop, 0)", ("bar", _bar));
        Bind(_barShadow, "Size", "Vector2(bar.Size.X + s.Extent * 2, bar.Size.Y + s.Extent * 2)", ("bar", _bar));
        Bind(_clipGeometry, "CornerRadius", "Vector2(p.Radius, p.Radius)");
        Bind(_outlineGeometry, "CornerRadius", "Vector2(Max(p.Radius - s.Half, 0), Max(p.Radius - s.Half, 0))");
        Bind(_strokeGeometry, "CornerRadius", "Vector2(Max(p.Radius - s.Stroke * 1.5, 0), Max(p.Radius - s.Stroke * 1.5, 0))");
        outline.StrokeThickness = 1;
        highlight.StrokeThickness = 1;
        _strokeShapes = (outline, highlight);

        // Small pill shown at the screen edge while the dock is tucked away.
        _handleBrush = C.CreateColorBrush();
        _handle = C.CreateSpriteVisual();
        _handle.Brush = _handleBrush;
        _handle.AnchorPoint = new Vector2(0.5f, 0.5f);
        _handle.Opacity = 0;
        _handleShape = C.CreateRoundedRectangleGeometry();
        _handle.Clip = C.CreateGeometricClip(_handleShape);
        _root.Children.InsertAtTop(_handle);

        _shadowSurface = _host.CreateSurface(8, 8);
        _shadowBrush.Source = C.CreateSurfaceBrush(_shadowSurface);
    }

    (CompositionSpriteShape outline, CompositionSpriteShape highlight) _strokeShapes;

    // Static values shared by the bar decoration expressions ("s." prefix).
    CompositionPropertySet? _shared;

    void Bind(CompositionObject target, string property, string expression, params (string name, CompositionObject obj)[] refs)
    {
        _shared ??= CreateShared();
        var e = C.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("p", _p);
        e.SetReferenceParameter("s", _shared);
        foreach (var (name, obj) in refs) e.SetReferenceParameter(name, obj);
        try { target.StartAnimation(property, e); }
        catch (Exception ex) { Log.Error($"Expression for {property} failed: {expression}", ex); }
    }

    CompositionPropertySet CreateShared()
    {
        var s = C.CreatePropertySet();
        s.InsertScalar("Half", 0.5f);
        s.InsertScalar("Stroke", 1);
        s.InsertScalar("Extent", 0);
        s.InsertScalar("Drop", 0);
        return s;
    }

    // ------------------------------------------------------------------ layout

    /// <param name="animate">Glide to the new sizes instead of jumping, deferring the surface repaints.</param>
    /// <param name="full">False for a live resize: only geometry changes, so skip theme and indicator work.</param>
    void Relayout(bool animate, bool full = true)
    {
        var mi = Native.GetMonitorInfo(_monitor);
        float scale = (float)Native.GetDpiForWindow(Handle) / 96f;
        if (scale <= 0) scale = 1;
        var old = _m;
        _m = Metrics.Compute(_settings, scale, mi.rcMonitor.Width, mi.rcMonitor.Height, FittedIconSize(scale, mi.rcMonitor.Width));

        int bottom = _settings.HideWindowsTaskbar ? mi.rcMonitor.Bottom : mi.rcWork.Bottom;
        _monitorBottom = mi.rcMonitor.Bottom;
        _winX = mi.rcMonitor.Left;
        _winY = bottom - _m.WinH;
        if (_winX != _lastWinX || _winY != _lastWinY || _m.WinW != _lastWinW || _m.WinH != _lastWinH)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _winX, _winY, _m.WinW, _m.WinH,
                Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW);
            (_lastWinX, _lastWinY, _lastWinW, _lastWinH) = (_winX, _winY, _m.WinW, _m.WinH);
        }
        _root.Size = new Vector2(_m.WinW, _m.WinH);

        // Everything that defines the dock's size glides together, on the compositor.
        SetScalar(_p, "Amount", _m.Amount, animate);
        SetScalar(_p, "R", _m.R, animate);
        SetScalar(_p, "W", _m.W, animate);
        SetScalar(_p, "BottomY", _m.BottomY, animate);
        SetScalar(_p, "BarTop", _m.BarTop, animate);
        SetScalar(_p, "BarH", _m.BarH, animate);
        SetScalar(_p, "DotY", _m.DotY, animate);
        SetScalar(_p, "Radius", _m.Radius, animate);
        SetScalar(_p, "SepW", _m.SepW, animate);
        _p.InsertScalar("LabelGap", _m.LabelGap);
        _shared!.InsertScalar("Stroke", _m.Stroke);
        _shared.InsertScalar("Half", _m.Stroke / 2);
        _shared.InsertScalar("Extent", _m.ShadowExtent);
        _shared.InsertScalar("Drop", MathF.Round(4 * _m.Scale));
        _strokeShapes.outline.StrokeThickness = _m.Stroke;
        _strokeShapes.highlight.StrokeThickness = _m.Stroke;
        var handleSize = new Vector2(MathF.Round(64 * scale), MathF.Max(3, MathF.Round(4 * scale)));
        _handle.Size = handleSize;
        _handleShape.Size = handleSize;
        _handleShape.CornerRadius = new Vector2(handleSize.Y / 2);
        _handle.Offset = new Vector3(_m.WinW / 2f, _m.WinH - MathF.Max(handleSize.Y, _m.Margin * 0.55f), 0);

        if (full) ApplyTheme();
        foreach (var slot in _slots)
        {
            ConfigureSlotVisual(slot);
            if (full) slot.LabelText = null;
        }
        LayoutSlots(animate);
        _panel.SetBottom(PanelBottom);
        _calendar?.SetBottom(PanelBottom);
        _panel.SyncMode(CurrentWallpaperMode?.Invoke() ?? WallpaperMode.Smart);

        // Icon surfaces and the shadow are stretched until the size settles, then redrawn crisp once.
        if (animate) Native.SetTimer(Handle, (IntPtr)RepaintTimer, 160, IntPtr.Zero);
        else RepaintSurfaces();
    }

    /// <summary>
    /// The largest icon size at which the whole dock still fits on the monitor while magnified. Without
    /// this a big size with many apps would push the Harbor button, and with it the quick panel, off-screen.
    /// </summary>
    double FittedIconSize(float scale, int monitorWidth)
    {
        int icons = _slots.Count(s => !s.Removing && !s.IsSeparator), seps = _slots.Count(s => !s.Removing && s.IsSeparator);
        if (icons == 0) return DockLimits.MaxIcon;
        float amount = _settings.Magnification ? (float)_settings.MagnificationAmount : 0;
        // In units of one icon, from Metrics and LayoutSlots. Magnification adds Amount * R at most (R = 2.9 W).
        float wide = _slots.Where(s => !s.Removing && !s.IsSeparator).Sum(s => s.Aspect - 1);
        float units = icons + wide + 0.4f + 0.14f * (icons + seps - 1) + 0.084f * seps + amount * 2.9f;
        return Math.Max(DockLimits.MinIcon, MathF.Floor((monitorWidth - (16 + seps) * scale) / units / scale));
    }

    void RepaintSurfaces()
    {
        if (_shadowRadius != _m.Radius || _shadowExtent != _m.ShadowExtent) PaintShadow();
        foreach (var slot in _slots)
        {
            PaintIcon(slot, force: false);
            UpdateIndicator(slot, animate: false);
        }
    }

    void SetScalar(CompositionPropertySet set, string name, float value, bool animate)
    {
        if (animate) { Spring(set, name, value); return; }
        // Stopping a running animation and setting the value straight after can leave the property wherever the animation
        // had got to, and setting that same value again later does nothing. Starting a new animation replaces the old one
        // outright, and a one millisecond animation is as good as setting it.
        set.InsertScalar(name, value);
        var jump = C.CreateScalarKeyFrameAnimation();
        jump.InsertKeyFrame(1, value);
        jump.Duration = TimeSpan.FromMilliseconds(1);
        set.StartAnimation(name, jump);
    }

    float PanelBottom => MagTop - 10 * _m.Scale;

    void PaintShadow()
    {
        float e = _m.ShadowExtent, r = _m.Radius;
        int side = (int)MathF.Ceiling((e + r) * 2 + 4);
        _shadowSurface.Resize(new Windows.Graphics.SizeInt32 { Width = side, Height = side });
        _host.Draw(_shadowSurface, dc => SurfacePainter.DrawShadow(dc, side, e, r));
        _shadowBrush.SetInsets(e + r);
        _shadowRadius = r;
        _shadowExtent = e;
    }

    void ApplyTheme()
    {
        var t = _theme;
        _tintBrush.ColorStops.Clear();
        _tintBrush.ColorStops.Add(C.CreateColorGradientStop(0, t.TintTop));
        _tintBrush.ColorStops.Add(C.CreateColorGradientStop(1, t.TintBottom));
        _strokeBrush.ColorStops.Clear();
        _strokeBrush.ColorStops.Add(C.CreateColorGradientStop(0, t.StrokeTop));
        _strokeBrush.ColorStops.Add(C.CreateColorGradientStop(0.5f, t.StrokeBottom));
        _strokeBrush.ColorStops.Add(C.CreateColorGradientStop(1, WColor.FromArgb((byte)(t.StrokeBottom.A / 2), t.StrokeBottom.R, t.StrokeBottom.G, t.StrokeBottom.B)));
        _outlineBrush.Color = t.Outline;
        _barShadow.Opacity = t.ShadowOpacity;
        _handleBrush.Color = WColor.FromArgb(150, t.Dot.R, t.Dot.G, t.Dot.B);
        foreach (var slot in _slots)
        {
            if (slot.IsSeparator && slot.Icon is SpriteVisual sep) sep.Brush = C.CreateColorBrush(t.Separator);
            UpdateIndicator(slot, animate: false);
            slot.LabelText = null;
        }
    }

    void LayoutSlots(bool animate)
    {
        var live = _slots.Where(s => !s.Removing).ToList();
        float total = 2 * _m.PadX;
        foreach (var s in live)
        {
            s.Width = s.IsSeparator ? _m.Gap * 0.6f + _m.SepW : _m.W * s.Aspect;
            SetScalar(s.Props, "Half", s.Width / 2, animate && !s.Fresh);
            total += s.Width;
        }
        total += _m.Gap * Math.Max(0, live.Count - 1);

        float left = MathF.Round((_m.WinW - total) / 2);
        float x = left + _m.PadX;
        foreach (var s in live)
        {
            s.TargetX = x + s.Width / 2;
            x += s.Width + _m.Gap;
            SetScalar(s.Props, "BaseX", s.TargetX, animate && !s.Fresh);
        }
        _barLeft = left;
        _barRight = left + total;
        SetScalar(_p, "BarL", _barLeft, animate);
        SetScalar(_p, "BarR", _barRight, animate);
    }

    void Spring(CompositionObject target, string property, float to, float periodMs = 55, float damping = 0.82f)
    {
        var a = C.CreateSpringScalarAnimation();
        a.FinalValue = to;
        a.Period = TimeSpan.FromMilliseconds(periodMs);
        a.DampingRatio = damping;
        target.StartAnimation(property, a);
    }

    void Tween(CompositionObject target, string property, float to, double ms, CompositionEasingFunction? easing = null)
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.InsertExpressionKeyFrame(0, "this.StartingValue");
        a.InsertKeyFrame(1, to, easing ?? _easeOut);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        target.StartAnimation(property, a);
    }

    // ------------------------------------------------------------------ items

    /// <summary>Diffs the dock against <paramref name="items"/>, animating insertions, removals and moves.</summary>
    public void SetItems(IReadOnlyList<DockItem> items)
    {
        var byKey = _slots.Where(s => !s.Removing).ToDictionary(s => s.Item.Key);
        var next = new List<Slot>(items.Count);
        bool anyNew = false;
        foreach (var item in items)
        {
            if (byKey.Remove(item.Key, out var slot))
            {
                slot.Item = item;
                PaintIcon(slot, force: false);
                UpdateIndicator(slot, animate: true);
            }
            else
            {
                slot = CreateSlot(item);
                anyNew = true;
            }
            next.Add(slot);
        }

        foreach (var gone in byKey.Values) RemoveSlot(gone);
        var removing = _slots.Where(s => s.Removing).ToList();
        _slots.Clear();
        _slots.AddRange(next);
        _slots.AddRange(removing);

        // More apps may no longer fit at this size (or fewer may fit a bigger one again).
        int monitorWidth = Native.GetMonitorInfo(_monitor).rcMonitor.Width;
        if (Metrics.IconPixels(_settings, _m.Scale, FittedIconSize(_m.Scale, monitorWidth)) != _m.W) Relayout(animate: true, full: false);

        // New slots start at their final position and grow in; existing ones glide over.
        LayoutSlotsForInsert(anyNew);
        if (_hoverSlot != null && !_slots.Contains(_hoverSlot)) _hoverSlot = null;
    }

    void LayoutSlotsForInsert(bool anyNew)
    {
        var fresh = _slots.Where(s => !s.Removing && s.Fresh).ToList();
        LayoutSlots(animate: true);
        foreach (var s in fresh)
        {
            s.Fresh = false;
            var a = C.CreateSpringScalarAnimation();
            a.FinalValue = 1;
            a.Period = TimeSpan.FromMilliseconds(60);
            a.DampingRatio = 0.62f;
            s.Props.StartAnimation("Appear", a);
        }
    }

    Slot CreateSlot(DockItem item)
    {
        var props = C.CreatePropertySet();
        props.InsertScalar("BaseX", 0);
        props.InsertScalar("Bounce", 0);
        props.InsertScalar("Appear", 0);
        props.InsertScalar("Press", 0);
        props.InsertScalar("Half", _m.W / 2);
        props.InsertScalar("CX", 0);
        props.InsertScalar("Mag", 1);
        float aspect = item.Kind == DockItemKind.Clock ? ClockAspect : 1;
        props.InsertScalar("Aspect", aspect);

        Visual icon;
        if (item.Kind == DockItemKind.Separator)
        {
            var sep = C.CreateSpriteVisual();
            sep.Brush = C.CreateColorBrush(_theme.Separator);
            icon = sep;
        }
        else
        {
            icon = C.CreateSpriteVisual();
        }
        var slot = new Slot { Item = item, Props = props, Icon = icon, Aspect = aspect };
        _slotsLayer.Children.InsertAtTop(icon);

        if (item.Kind != DockItemKind.Separator)
        {
            slot.DotColor = C.CreateColorBrush(_theme.Dot);
            slot.Dot = C.CreateSpriteVisual();
            slot.Dot.Brush = slot.DotColor;
            slot.Dot.AnchorPoint = new Vector2(0.5f, 0.5f);
            slot.Dot.Opacity = 0;
            // Pill shape: a rounded clip whose size tracks the sprite, so width changes animate cleanly.
            var pill = C.CreateRoundedRectangleGeometry();
            slot.DotShape = pill;
            slot.Dot.Clip = C.CreateGeometricClip(pill);
            var follow = C.CreateExpressionAnimation("dot.Size");
            follow.SetReferenceParameter("dot", slot.Dot);
            pill.StartAnimation("Size", follow);
            _slotsLayer.Children.InsertAtBottom(slot.Dot);
        }

        ConfigureSlotVisual(slot);
        BindSlot(slot);
        PaintIcon(slot, force: true);
        UpdateIndicator(slot, animate: false);
        _slots.Add(slot);
        return slot;
    }

    void ConfigureSlotVisual(Slot slot)
    {
        if (slot.IsSeparator)
        {
            slot.Icon.AnchorPoint = new Vector2(0.5f, 1f);
            return;
        }
        slot.Icon.AnchorPoint = new Vector2(0.5f, 1f);
        if (slot.DotShape != null) slot.DotShape.CornerRadius = new Vector2(_m.DotD / 2);
    }

    void BindSlot(Slot slot)
    {
        // Per-slot derived values live on the slot's property set so every visual of the slot shares them.
        string lo = "(s.BaseX - s.Half)", hi = "(s.BaseX + s.Half)";
        BindSlotExpr(slot.Props, "CX", $"s.BaseX + ({Disp(lo)} + {Disp(hi)}) * 0.5", slot.Props);
        BindSlotExpr(slot.Props, "Mag", $"1 + ({Disp(hi)} - {Disp(lo)}) / (2 * s.Half)", slot.Props);
        if (slot.IsSeparator)
        {
            BindSlotExpr(slot.Icon, "Size", "Vector2(p.SepW, p.W * 0.62)", slot.Props);
            BindSlotExpr(slot.Icon, "Offset", "Vector3(s.CX, p.BottomY - p.W * 0.19, 0)", slot.Props);
            BindSlotExpr(slot.Icon, "Opacity", "s.Appear", slot.Props);
            return;
        }
        BindSlotExpr(slot.Icon, "Size", "Vector2(p.W * s.Aspect, p.W)", slot.Props);
        BindSlotExpr(slot.Icon, "CenterPoint", "Vector3(p.W * s.Aspect * 0.5, p.W, 0)", slot.Props);
        BindSlotExpr(slot.Icon, "Offset", "Vector3(s.CX, p.BottomY - s.Bounce, 0)", slot.Props);
        BindSlotExpr(slot.Icon, "Scale", "Vector3(s.Mag * s.Appear * (1 - 0.1 * s.Press), s.Mag * s.Appear * (1 - 0.1 * s.Press), 1)", slot.Props);
        if (slot.Dot != null)
            BindSlotExpr(slot.Dot, "Offset", "Vector3(s.CX, p.DotY, 0)", slot.Props);
    }

    void BindSlotExpr(CompositionObject target, string property, string expression, CompositionPropertySet s)
    {
        var e = C.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("p", _p);
        e.SetReferenceParameter("s", s);
        try { target.StartAnimation(property, e); }
        catch (Exception ex) { Log.Error($"Slot expression for {property} failed: {expression}", ex); }
    }

    void RemoveSlot(Slot slot)
    {
        slot.Removing = true;
        if (slot == _labelSlot) HideLabel();
        if (slot == _hoverSlot) _hoverSlot = null;
        var batch = C.CreateScopedBatch(CompositionBatchTypes.Animation);
        Tween(slot.Props, "Appear", 0, 180, _easeInOut);
        if (slot.Dot != null) Tween(slot.Dot, "Opacity", 0, 120);
        batch.End();
        batch.Completed += (_, _) =>
        {
            _slotsLayer.Children.Remove(slot.Icon);
            if (slot.Dot != null) _slotsLayer.Children.Remove(slot.Dot);
            if (slot.Label != null) _labelsLayer.Children.Remove(slot.Label);
            slot.Icon.Dispose();
            slot.IconSurface?.Dispose();
            _slots.Remove(slot);
        };
    }

    void PaintIcon(Slot slot, bool force)
    {
        if (slot.IsSeparator || slot.Icon is not SpriteVisual sprite) return;
        var item = slot.Item;
        if (!force && slot.PaintedIconVersion == item.IconVersion && slot.PaintedSize == _m.IconPx) return;
        slot.PaintedIconVersion = item.IconVersion;
        slot.PaintedSize = _m.IconPx;

        int px = _m.IconPx, pxWide = (int)MathF.Ceiling(px * slot.Aspect);
        if (slot.IconSurface == null)
        {
            slot.IconSurface = _host.CreateSurface(pxWide, px);
            var brush = C.CreateSurfaceBrush(slot.IconSurface);
            brush.Stretch = CompositionStretch.Fill;
            brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            sprite.Brush = brush;
            if (item.Kind == DockItemKind.Clock)
            {
                // The bar is see-through, so the text can end up over anything. A soft dark halo keeps light text readable
                // over a bright window behind it; dark text on the light theme already is, and a shadow would smudge it.
                if (_theme.Dark)
                {
                    var halo = C.CreateDropShadow();
                    halo.Mask = brush;
                    halo.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                    halo.Opacity = 0.75f;
                    halo.BlurRadius = 4 * _m.Scale;
                    halo.Offset = new Vector3(0, 0.5f * _m.Scale, 0);
                    sprite.Shadow = halo;
                }
            }
            else
            {
                var shadow = C.CreateDropShadow();
                shadow.Mask = brush;
                shadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                shadow.Opacity = 0.32f;
                shadow.BlurRadius = 7 * _m.Scale;
                shadow.Offset = new Vector3(0, 2.5f * _m.Scale, 0);
                sprite.Shadow = shadow;
            }
        }
        else
        {
            slot.IconSurface.Resize(new Windows.Graphics.SizeInt32 { Width = pxWide, Height = px });
        }

        _host.Draw(slot.IconSurface, dc =>
        {
            switch (item.Kind)
            {
                case DockItemKind.Start:
                    SurfacePainter.DrawStartTile(dc, px);
                    break;
                case DockItemKind.Media:
                    SurfacePainter.DrawMediaTile(dc, px, item.Icon, item.IsActive);
                    break;
                case DockItemKind.Harbor:
                    SurfacePainter.DrawHarborTile(dc, px);
                    break;
                case DockItemKind.Clock:
                    var now = DateTime.Now;
                    SurfacePainter.DrawClock(dc, pxWide, px, now.ToString("t"), now.ToString("ddd d MMM"), _theme.LabelText,
                        WColor.FromArgb(225, _theme.LabelText.R, _theme.LabelText.G, _theme.LabelText.B));
                    break;
                default:
                    if (item.Icon != null)
                    {
                        // A little breathing room so icons with full-bleed artwork match the shell's optical size.
                        float inset = px * 0.04f;
                        SurfacePainter.DrawImage(dc, item.Icon, new RawRectF(inset, inset, px - inset, px - inset));
                    }
                    else SurfacePainter.DrawLetterTile(dc, item.Name, px);
                    break;
            }
        });
    }

    void UpdateIndicator(Slot slot, bool animate)
    {
        if (slot.Dot == null || slot.DotColor == null) return;
        var item = slot.Item;
        bool running = (item.IsRunning || (item.Kind == DockItemKind.Media && item.IsActive)) && _settings.ShowRunningIndicators;
        bool active = running && item.IsActive;
        var size = new Vector2(active ? _m.PillW : _m.DotD, _m.DotD);
        var color = item.NeedsAttention ? WColor.FromArgb(255, 255, 159, 67) : active ? _theme.Accent : _theme.Dot;

        if (animate && (running != slot.WasRunning || active != slot.WasActive))
        {
            var sa = C.CreateVector2KeyFrameAnimation();
            sa.InsertExpressionKeyFrame(0, "this.StartingValue");
            sa.InsertKeyFrame(1, size, _easeOut);
            sa.Duration = TimeSpan.FromMilliseconds(260);
            slot.Dot.StartAnimation("Size", sa);
            var ca = C.CreateColorKeyFrameAnimation();
            ca.InsertExpressionKeyFrame(0, "this.StartingValue");
            ca.InsertKeyFrame(1, color, _easeOut);
            ca.Duration = TimeSpan.FromMilliseconds(260);
            slot.DotColor.StartAnimation("Color", ca);
            Tween(slot.Dot, "Opacity", running ? 1 : 0, 220);
        }
        else
        {
            slot.Dot.Size = size;
            slot.DotColor.Color = color;
            slot.Dot.Opacity = running ? 1 : 0;
        }

        // Stop a launch bounce as soon as the app shows a window.
        if (running && !slot.WasRunning && slot.Bouncing) StopBounce(slot);
        slot.WasRunning = running;
        slot.WasActive = active;
    }

    public void Refresh(DockItem item)
    {
        var slot = _slots.FirstOrDefault(s => !s.Removing && s.Item.Key == item.Key);
        if (slot == null) return;
        slot.Item = item;
        PaintIcon(slot, force: false);
        UpdateIndicator(slot, animate: true);
    }

    /// <summary>The launch bounce: a couple of eased hops that stop early once a window appears.</summary>
    public void Bounce(DockItem item, int hops = 3)
    {
        if (!_settings.BounceOnLaunch) return;
        var slot = _slots.FirstOrDefault(s => !s.Removing && s.Item.Key == item.Key);
        if (slot == null) return;
        float h = _m.W * 0.5f;
        var a = C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 0);
        a.InsertKeyFrame(0.42f, h, C.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.6f), new Vector2(0.35f, 1f)));
        a.InsertKeyFrame(0.84f, 0, C.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0f), new Vector2(0.9f, 0.4f)));
        a.InsertKeyFrame(1f, 0);
        a.Duration = TimeSpan.FromMilliseconds(620);
        a.IterationBehavior = AnimationIterationBehavior.Count;
        a.IterationCount = hops;
        slot.Props.StartAnimation("Bounce", a);
        slot.Bouncing = true;
    }

    void StopBounce(Slot slot)
    {
        slot.Bouncing = false;
        Tween(slot.Props, "Bounce", 0, 200);
    }

    // ------------------------------------------------------------------ labels

    void ShowLabel(Slot slot)
    {
        if (HasPanel(slot)) { HideLabel(); return; }
        if (!_settings.ShowLabels || slot.IsSeparator) return;
        if (_labelSlot == slot && slot.Label != null) return;
        HideLabel();
        _labelSlot = slot;

        string text = slot.Item.Name;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (slot.Label == null || slot.LabelText != text)
        {
            var (layout, tw, th) = SurfacePainter.MeasureLabel(text, _m.LabelFont);
            using (layout)
            {
                int w = (int)MathF.Ceiling(tw + _m.LabelPadX * 2), h = (int)MathF.Ceiling(th + _m.LabelPadY * 2);
                var surface = _host.CreateSurface(w, h);
                _host.Draw(surface, dc => SurfacePainter.DrawLabel(dc, layout, tw, th, _m.LabelPadX, _m.LabelPadY,
                    _theme.LabelBackground, _theme.LabelBorder, _theme.LabelText));
                if (slot.Label == null)
                {
                    slot.Label = C.CreateSpriteVisual();
                    slot.Label.AnchorPoint = new Vector2(0.5f, 1f);
                    var shadow = C.CreateDropShadow();
                    shadow.Opacity = 0.25f;
                    shadow.BlurRadius = 10 * _m.Scale;
                    shadow.Offset = new Vector3(0, 2 * _m.Scale, 0);
                    slot.Label.Shadow = shadow;
                    BindSlotExpr(slot.Label, "Offset",
                        "Vector3(s.CX, p.BottomY - p.W * s.Mag - p.LabelGap - s.Bounce, 0)", slot.Props);
                    _labelsLayer.Children.InsertAtTop(slot.Label);
                }
                var brush = C.CreateSurfaceBrush(surface);
                slot.Label.Brush = brush;
                ((DropShadow)slot.Label.Shadow).Mask = brush;
                slot.Label.Size = new Vector2(w, h);
                slot.LabelText = text;
            }
        }
        slot.Label.Opacity = 0;
        Tween(slot.Label, "Opacity", 1, 140);
    }

    void HideLabel()
    {
        if (_labelSlot?.Label != null) Tween(_labelSlot.Label, "Opacity", 0, 110);
        _labelSlot = null;
    }

    // ------------------------------------------------------------------ hit testing

    float HoverValue => _hovering ? 1f : 0f;

    float DispAt(float x)
    {
        float t = MathF.Min(MathF.Abs(x - _mouseX) / _m.R, 1);
        return _m.Amount * HoverValue * _m.R * (t * 0.5f + MathF.Sin(t * MathF.PI) / (2 * MathF.PI)) * (x < _mouseX ? -1 : 1);
    }

    /// <summary>Magnified centre and size of a slot, mirroring the composition expressions.</summary>
    (float center, float size) Footprint(Slot s)
    {
        float half = MathF.Max(s.Width / 2, 1), lo = s.TargetX - half, hi = s.TargetX + half;
        float dLo = DispAt(lo), dHi = DispAt(hi);
        return (s.TargetX + (dLo + dHi) * 0.5f, _m.W * (1 + (dHi - dLo) / (2 * half)));
    }

    float MagLeft => _barLeft + DispAt(_barLeft);
    float MagRight => _barRight + DispAt(_barRight);
    float MagTop => _m.BottomY - _m.W * (1 + _m.Amount);
    /// <summary>Distance from the window top to the bottom of the monitor (the taskbar may sit below the window).</summary>
    float ScreenBottom => _monitorBottom - _winY;

    /// <summary>Entering this starts magnification.</summary>
    bool InHotZone(float x, float y)
    {
        float top = _hovering ? MagTop - 2 * _m.Scale : _m.BarTop;
        return x >= MagLeft - 1 && x <= MagRight + 1 && y >= top && y <= _m.WinH;
    }

    /// <summary>Magnification ends only after leaving this slightly larger area, so edges never flicker.</summary>
    bool InHoverExitZone(float x, float y)
    {
        float pad = 10 * _m.Scale;
        return x >= MagLeft - pad && x <= MagRight + pad && y >= MagTop - pad && y <= _m.WinH + pad;
    }

    /// <summary>While hidden: the strip at the bottom of the screen under the dock, taskbar included.</summary>
    bool InRevealZone(float x, float y)
    {
        float slack = (_barRight - _barLeft) * 0.1f;
        return x >= _barLeft - slack && x <= _barRight + slack && y >= _m.WinH - (_m.Margin + 10 * _m.Scale) && y <= ScreenBottom + 1;
    }

    /// <summary>While shown: anywhere near the dock keeps it up (label space above, taskbar below).</summary>
    bool InEngageZone(float x, float y)
    {
        float pad = 24 * _m.Scale;
        return x >= MagLeft - pad && x <= MagRight + pad && y >= MagTop - _m.LabelGap - 40 * _m.Scale && y <= ScreenBottom + 1;
    }

    Slot? HitTest(float x, float y)
    {
        if (y < _m.BarTop - _m.W * _m.Amount - 4 * _m.Scale) return null;
        foreach (var s in _slots)
        {
            if (s.Removing || s.IsSeparator) continue;
            var (cx, size) = Footprint(s);
            if (MathF.Abs(x - cx) <= (size * s.Aspect + _m.Gap) / 2 && y >= _m.BottomY - size - 2) return s;
        }
        return null;
    }

    /// <summary>Screen rectangle of an item's icon at rest, e.g. to anchor a menu above it.</summary>
    public Native.RECT? GetItemScreenRect(DockItem item)
    {
        var s = _slots.FirstOrDefault(x => !x.Removing && x.Item.Key == item.Key);
        if (s == null) return null;
        var (cx, size) = Footprint(s);
        float half = size * s.Aspect / 2;
        return new Native.RECT(_winX + (int)(cx - half), _winY + (int)(_m.BottomY - size), _winX + (int)(cx + half), _winY + (int)_m.BottomY);
    }

    // ------------------------------------------------------------------ input

    void SetClickThrough(bool transparent)
    {
        if (_transparent == transparent) return;
        _transparent = transparent;
        uint ex = Native.GetExStyle(Handle);
        Native.SetExStyle(Handle, transparent ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
    }

    void OnCursor()
    {
        if (!Native.GetCursorPos(out var pt)) return;
        float x = pt.X - _winX, y = pt.Y - _winY;

        UpdateAutoHide(
            near: !_autoHidden && InEngageZone(x, y),
            inRevealZone: _autoHidden && !_hiddenByFullscreen && InRevealZone(x, y));

        var open = OpenOne;
        bool panelOpen = open != null, panelPressed = open?.IsPressed == true;
        float iconHalf = (_panelSlot?.Width ?? _m.W) * (1 + _m.Amount) / 2 + 6 * _m.Scale;
        bool inPanelZone = panelOpen && open!.InKeepZone(x, y, iconHalf, _m.WinH);
        open?.OnMove(x, y);

        bool interactive = !_autoHidden && !_hiddenByFullscreen;
        bool inside = (interactive && (_hovering ? InHoverExitZone(x, y) : InHotZone(x, y))) || _pressed != null || _menuOpen || _previewOpen
                      || inPanelZone || panelPressed;
        if (inside)
        {
            SetClickThrough(false);
            if (!_hovering)
            {
                _hovering = true;
                Tween(_p, "Hover", 1, 170);
                HoverChanged?.Invoke(true);
            }
            // While a popup is in use the dock holds still under it rather than following the pointer.
            bool holdStill = (_previewOpen || panelOpen) && !InHotZone(x, y) || panelPressed;
            if (x != _mouseX && !holdStill)
            {
                _mouseX = x;
                _p.InsertScalar("MouseX", x);
            }
            var hit = _menuOpen || holdStill ? _hoverSlot : HitTest(x, y);
            if (hit != _hoverSlot)
            {
                _hoverSlot = hit;
                if (hit != null) ShowLabel(hit);
                else HideLabel();
                HoverItemChanged?.Invoke(hit?.Item);
                // A short rest before opening, so passing over the icon doesn't flash the panel.
                if (hit != null && HasPanel(hit) && !IsPanelOpenFor(hit))
                    Native.SetTimer(Handle, (IntPtr)PanelTimer, 110, IntPtr.Zero);
                else
                    Native.KillTimer(Handle, (IntPtr)PanelTimer);
            }
            if (_pressed != null && !_dragging && (Math.Abs(pt.X - _pressPoint.X) > 6 * _m.Scale || Math.Abs(pt.Y - _pressPoint.Y) > 6 * _m.Scale))
                BeginDrag();
            if (_dragging) UpdateDrag(x, y);
        }
        else
        {
            SetClickThrough(true);
            if (_hovering) EndHover();
        }

        if (OpenOne is { IsPressed: false })
        {
            long now = Environment.TickCount64;
            if (inPanelZone || (_hoverSlot != null && _hoverSlot == _panelSlot) || now < _panelPinnedUntil) _panelAwaySince = 0;
            else if (_panelAwaySince == 0) _panelAwaySince = now;
            else if (now - _panelAwaySince >= 280) ClosePanel();
        }
    }

    /// <summary>The one panel showing on this dock, if any.</summary>
    DockFlyout? OpenOne => _panel.IsOpen ? _panel : _calendar is { IsOpen: true } ? _calendar : null;

    static bool HasPanel(Slot slot) => slot.Item.Kind is DockItemKind.Harbor or DockItemKind.Clock;

    bool IsPanelOpenFor(Slot slot) => slot.Item.Kind == DockItemKind.Harbor ? _panel.IsOpen : _calendar is { IsOpen: true };

    void OpenPanelFor(Slot slot)
    {
        if (!HasPanel(slot) || IsPanelOpenFor(slot) || _autoHidden) return;
        ClosePanel();
        HideLabel();
        _panelAwaySince = 0;
        _panelSlot = slot;
        var (cx, _) = Footprint(slot);
        if (slot.Item.Kind == DockItemKind.Harbor)
            _panel.Open(_theme, _m.Scale, CurrentWallpaperMode?.Invoke() ?? WallpaperMode.Smart, cx, PanelBottom, _m.WinW);
        else
            (_calendar ??= new CalendarPanel(_host, _dockLayer)).Open(_theme, _m.Scale, cx, PanelBottom, _m.WinW);
    }

    /// <summary>Developer aid: holds the quick panel open for a while with no pointer involved.</summary>
    public void ShowPanelForTest(int ms, DockItemKind kind)
    {
        var slot = _slots.FirstOrDefault(s => s.Item.Kind == kind);
        if (slot == null) return;
        _panelPinnedUntil = Environment.TickCount64 + ms;
        _engagedUntil = _panelPinnedUntil;
        ApplyHidden(false);
        OpenPanelFor(slot);
        var (cx, _) = Footprint(slot);
        Log.Info($"Panel test: anchorX={_winX + cx:0} bottom={_winY + PanelBottom:0} iconY={_winY + _m.BottomY - _m.W / 2:0} scale={_m.Scale:0.###} winX={_winX} winY={_winY} winW={_m.WinW} winH={_m.WinH}");
    }

    /// <summary>For the developer test: where this dock's bar and an item are.</summary>
    public string DescribeForTest() =>
        $"Dock test: device={DeviceName} scale={_m.Scale:0.###} barL={_winX + _barLeft:0} barR={_winX + _barRight:0} barT={_winY + _m.BarTop:0} barB={_winY + _m.BarTop + _m.BarH:0} winX={_winX} winY={_winY} winW={_m.WinW} winH={_m.WinH} hidden={_autoHidden}";

    void ClosePanel()
    {
        Native.KillTimer(Handle, (IntPtr)PanelTimer);
        var open = OpenOne;
        if (open == null) return;
        if (open.IsPressed) Native.ReleaseCapture();
        open.Close();
    }

    // ------------------------------------------------------------------ staying on top

    /// <summary>
    /// Being topmost isn't enough: any other always-on-top window that appears later sits above the dock.
    /// So when a real app window has climbed above it and overlaps it, raise the dock again. Menus, tooltips
    /// and other transient popups don't count (they'd be covered otherwise), and neither do Harbor's own.
    /// Windows in a higher system band can't be beaten from a normal app, so those are noted and left alone.
    /// </summary>
    public void KeepOnTop()
    {
        if (_hiddenByFullscreen || _menuOpen || _previewOpen || Handle == IntPtr.Zero) return;
        var above = AppWindowAbove();
        if (above == IntPtr.Zero) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        if (AppWindowAbove() == above && _cantBeat.Add(above))
            Log.Info($"Dock can't rise above [{Native.GetWindowClass(above)}] '{Native.GetWindowTitle(above)}'");
    }

    IntPtr AppWindowAbove()
    {
        float top = _hovering ? MagTop : _m.BarTop;
        var dock = new Native.RECT(_winX + (int)MagLeft, _winY + (int)top, _winX + (int)MagRight, _winY + (int)(_m.BarTop + _m.BarH));
        uint me = (uint)Environment.ProcessId;
        for (var h = Native.GetWindow(Handle, 3 /* GW_HWNDPREV */); h != IntPtr.Zero; h = Native.GetWindow(h, 3))
        {
            if (_cantBeat.Contains(h) || !IsRealAppWindow(h)) continue;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) continue;
            var r = Native.GetVisibleBounds(h);
            if (r.Right > dock.Left && r.Left < dock.Right && r.Bottom > dock.Top && r.Top < dock.Bottom) return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>Taskbar windows, plus resizable floating ones like picture-in-picture. Never menus or tooltips.</summary>
    static bool IsRealAppWindow(IntPtr h)
    {
        if (WindowTracker.IsTaskbarWindow(h)) return true;
        return Native.IsWindowVisible(h) && !Native.IsCloaked(h)
            && (Native.GetStyle(h) & Native.WS_THICKFRAME) != 0
            && Native.GetWindow(h, Native.GW_OWNER) == IntPtr.Zero
            && Native.GetWindowTextLength(h) > 0;
    }

    /// <summary>
    /// Auto-hide with hysteresis: reveal only after the cursor rests at the bottom edge briefly, stay up
    /// while it is anywhere near the dock, and slide away only after it has been gone for a moment.
    /// </summary>
    void UpdateAutoHide(bool near, bool inRevealZone)
    {
        long now = Environment.TickCount64;
        if (!_autoHideWanted)
        {
            _revealDwellStart = 0;
            if (_autoHidden) ApplyHidden(false);
            return;
        }
        if (_autoHidden)
        {
            if (!inRevealZone) { _revealDwellStart = 0; return; }
            if (_revealDwellStart == 0) _revealDwellStart = now;
            if (now - _revealDwellStart >= RevealDwellMs)
            {
                _revealDwellStart = 0;
                _engagedUntil = now + HideGraceMs;
                ApplyHidden(false);
            }
            return;
        }
        if (near || _menuOpen || _previewOpen || _pressed != null || OpenOne != null) _engagedUntil = now + HideGraceMs;
        else if (now >= _engagedUntil) ApplyHidden(true);
    }

    void EndHover()
    {
        _hovering = false;
        Tween(_p, "Hover", 0, 260, _easeInOut);
        HideLabel();
        if (_hoverSlot != null)
        {
            _hoverSlot = null;
            HoverItemChanged?.Invoke(null);
        }
        HoverChanged?.Invoke(false);
    }

    public event Action<bool>? HoverChanged;
    /// <summary>The item under the cursor changed (null when over empty dock space).</summary>
    public event Action<DockItem?>? HoverItemChanged;

    bool _previewOpen;
    /// <summary>While a window preview is open the dock stays revealed and magnified.</summary>
    public void SetPreviewOpen(bool open)
    {
        _previewOpen = open;
        if (!open) OnCursor();
    }

    const float ClockAspect = 1.9f;
    static readonly List<DockWindow> s_docks = new();
    static long s_lastRawInput;
    QuickPanel _panel = null!;
    CalendarPanel? _calendar;
    Slot? _panelSlot;
    long _panelAwaySince, _panelPinnedUntil;
    int _topCheckTick;
    readonly HashSet<IntPtr> _cantBeat = new();
    float _shadowRadius, _shadowExtent;
    int _lastWinW, _lastWinH, _lastWinX, _lastWinY;
    const int PollTimer = 1, RepaintTimer = 2, PanelTimer = 3;

    bool _menuOpen;
    /// <summary>While a context menu is open the dock stays magnified around the item.</summary>
    public void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        if (!open) OnCursor();
    }

    // ------------------------------------------------------------------ drag to reorder

    void BeginDrag()
    {
        if (_pressed == null || _pressed.Item.Kind != DockItemKind.App || !_pressed.Item.IsPinned) return;
        _dragging = true;
        HideLabel();
        Tween(_pressed.Props, "Press", 0, 120);
        // Lift the dragged icon slightly so it reads as picked up.
        Tween(_pressed.Props, "Bounce", _m.W * 0.12f, 140);
    }

    bool _dragAway;

    void UpdateDrag(float x, float y)
    {
        if (_pressed == null) return;
        // Pulled well above the dock: show that letting go will remove it.
        bool away = y < _m.BarTop - _m.W * 1.1f;
        if (away != _dragAway)
        {
            _dragAway = away;
            Tween(_pressed.Icon, "Opacity", away ? 0.45f : 1f, 140);
            Tween(_pressed.Props, "Bounce", away ? _m.W * 0.45f : _m.W * 0.12f, 160);
        }
        _pressed.Props.InsertScalar("BaseX", x); // follow the cursor directly
        // Find the insertion index among pinned app slots based on cursor position.
        var live = _slots.Where(s => !s.Removing).ToList();
        int from = live.IndexOf(_pressed);
        int to = from;
        for (int i = 0; i < live.Count; i++)
        {
            if (live[i] == _pressed || !live[i].Item.IsPinned) continue;
            if (i < from && x < live[i].TargetX) { to = i; break; }
            if (i > from && x > live[i].TargetX) to = i;
        }
        if (to != from)
        {
            _slots.Remove(_pressed);
            int insertAt = _slots.IndexOf(live[to]);
            if (to > from) insertAt++;
            _slots.Insert(Math.Clamp(insertAt, 0, _slots.Count), _pressed);
            float keep = x;
            LayoutSlots(animate: true);
            _pressed.Props.InsertScalar("BaseX", keep);
        }
    }

    void EndDrag()
    {
        if (_pressed == null) return;
        _dragging = false;
        var slot = _pressed;
        if (_dragAway)
        {
            _dragAway = false;
            // A quick "poof": swell and fade, then the controller unpins and the dock closes the gap.
            Tween(slot.Props, "Appear", 1.35f, 160);
            Tween(slot.Icon, "Opacity", 0, 160);
            ItemDraggedOff?.Invoke(slot.Item);
            return;
        }
        Tween(slot.Props, "Bounce", 0, 160);
        LayoutSlots(animate: true);
        var pinnedOrder = _slots.Where(s => !s.Removing && s.Item.IsPinned).ToList();
        ItemReordered?.Invoke(slot.Item, pinnedOrder.IndexOf(slot));
    }

    // ------------------------------------------------------------------ visibility

    /// <summary>Hides the dock entirely, e.g. while a fullscreen app or game is in front.</summary>
    public void SetFullscreenHidden(bool hidden)
    {
        if (_hiddenByFullscreen == hidden) return;
        _hiddenByFullscreen = hidden;
        if (hidden)
        {
            if (_hovering) EndHover();
            ClosePanel();
            SetClickThrough(true);
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }
        else
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
    }

    /// <summary>
    /// The controller's wish to get out of the way (windows overlap, Start is open, "always" mode).
    /// The dock still decides when to actually move, so the cursor near it always wins.
    /// </summary>
    public void SetAutoHideWanted(bool wanted)
    {
        if (_autoHideWanted == wanted) return;
        _autoHideWanted = wanted;
        if (wanted && !_hovering && !_menuOpen && !_previewOpen) _engagedUntil = 0;
        OnCursor();
    }

    void ApplyHidden(bool hidden)
    {
        if (_autoHidden == hidden) return;
        _autoHidden = hidden;
        if (hidden && _hovering) EndHover();
        if (hidden) ClosePanel();
        var a = C.CreateVector3KeyFrameAnimation();
        a.InsertExpressionKeyFrame(0, "this.StartingValue");
        a.InsertKeyFrame(1, new Vector3(0, hidden ? _m.WinH - _m.BarTop + _m.ShadowExtent : 0, 0), hidden ? _easeInOut : _easeOut);
        a.Duration = TimeSpan.FromMilliseconds(hidden ? 300 : 230);
        _dockLayer.StartAnimation("Offset", a);
        Tween(_handle, "Opacity", hidden ? 1 : 0, hidden ? 420 : 120);
    }

    public bool IsAutoHidden => _autoHidden;

    public void ApplySettings(DockSettings settings)
    {
        _settings = settings;
        _theme = DockTheme.Resolve(settings);
        Relayout(animate: true);
    }

    public void OnThemeChanged()
    {
        _theme = DockTheme.Resolve(_settings);
        _panel.Invalidate();
        _calendar?.Invalidate();
        ApplyTheme();
        foreach (var s in _slots) PaintIcon(s, force: true);
    }

    // ------------------------------------------------------------------ messages

    protected override bool WndProc(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case Native.WM_INPUT:
            {
                // A gaming mouse reports up to 1000 times a second, wherever the pointer is. Raw input is only here
                // to notice the pointer arriving; once it's over the dock, WM_MOUSEMOVE tracks it at full rate.
                long now = Environment.TickCount64;
                if (now - s_lastRawInput >= 10)
                {
                    s_lastRawInput = now;
                    for (int i = 0; i < s_docks.Count; i++) s_docks[i].OnCursor();
                }
                return false; // DefWindowProc releases the raw input buffer
            }

            case Native.WM_MOUSEMOVE:
                OnCursor();
                return true;

            case Native.WM_TIMER:
                switch ((int)wParam)
                {
                    case RepaintTimer:
                        Native.KillTimer(Handle, (IntPtr)RepaintTimer);
                        RepaintSurfaces();
                        break;
                    case PanelTimer:
                        Native.KillTimer(Handle, (IntPtr)PanelTimer);
                        if (_hoverSlot != null) OpenPanelFor(_hoverSlot);
                        break;
                    default:
                        OnCursor();
                        if (++_topCheckTick % 3 == 0) KeepOnTop();
                        if (_topCheckTick % 10 == 0) OpenOne?.Refresh();
                        break;
                }
                return true;

            case Native.WM_MOUSEACTIVATE:
                result = (IntPtr)Native.MA_NOACTIVATE;
                return true;

            case Native.WM_LBUTTONDOWN:
            {
                Native.GetCursorPos(out _pressPoint);
                if (OpenOne is { } open && open.Contains(Native.LoWord(lParam), Native.HiWord(lParam)))
                {
                    open.OnDown(Native.LoWord(lParam), Native.HiWord(lParam));
                    Native.SetCapture(Handle);
                    return true;
                }
                var hit = HitTest(Native.LoWord(lParam), Native.HiWord(lParam));
                if (hit != null)
                {
                    _pressed = hit;
                    Tween(hit.Props, "Press", 1, 90);
                    Native.SetCapture(Handle);
                    ItemPressed?.Invoke(hit.Item);
                }
                return true;
            }

            case Native.WM_LBUTTONUP:
            {
                if (OpenOne is { IsPressed: true } open)
                {
                    Native.ReleaseCapture();
                    open.OnUp(Native.LoWord(lParam), Native.HiWord(lParam));
                    OnCursor();
                    return true;
                }
                var pressed = _pressed;
                _pressed = null;
                Native.ReleaseCapture();
                if (pressed == null) return true;
                Tween(pressed.Props, "Press", 0, 160);
                if (_dragging)
                {
                    _pressed = pressed;
                    EndDrag();
                    _pressed = null;
                    return true;
                }
                var hit = HitTest(Native.LoWord(lParam), Native.HiWord(lParam));
                // Swallow accidental double clicks so an app isn't minimized right after restoring.
                if (hit == pressed && (DateTime.UtcNow - _lastClick).TotalMilliseconds > 300)
                {
                    _lastClick = DateTime.UtcNow;
                    if (hit.Item.Kind == DockItemKind.Clock)
                    {
                        // Hovering has usually opened it already; a click just doesn't make you wait.
                        _panelPinnedUntil = Environment.TickCount64 + 600;
                        OpenPanelFor(hit);
                    }
                    else
                    {
                        if (hit.Item.Kind == DockItemKind.Harbor) ClosePanel(); // the app is about to open where it was
                        ItemInvoked?.Invoke(hit.Item, DockButton.Left);
                    }
                }
                OnCursor();
                return true;
            }

            case Native.WM_MBUTTONUP:
            {
                var hit = HitTest(Native.LoWord(lParam), Native.HiWord(lParam));
                if (hit != null) ItemInvoked?.Invoke(hit.Item, DockButton.Middle);
                return true;
            }

            case Native.WM_RBUTTONUP:
            {
                if (OpenOne?.Contains(Native.LoWord(lParam), Native.HiWord(lParam)) == true) return true;
                ClosePanel();
                Native.GetCursorPos(out var pt);
                var hit = HitTest(Native.LoWord(lParam), Native.HiWord(lParam));
                if (hit != null && hit != _labelSlot) ShowLabel(hit);
                ContextMenuRequested?.Invoke(hit?.Item, pt);
                return true;
            }

            case Native.WM_MOUSEWHEEL:
            {
                int dir = Math.Sign(Native.HiWord(wParam));
                // Wheel messages carry screen coordinates.
                Native.GetCursorPos(out var wp);
                if (OpenOne is { } open && open.Contains(wp.X - _winX, wp.Y - _winY)) open.OnWheel(wp.X - _winX, wp.Y - _winY, dir);
                else if ((Native.GetKeyState(Native.VK_CONTROL) & 0x8000) != 0) IconSizeWheel?.Invoke(dir);
                else
                {
                    var hit = HitTest(wp.X - _winX, wp.Y - _winY);
                    if (hit != null) ItemScrolled?.Invoke(hit.Item, dir);
                }
                return true;
            }

            case Native.WM_DPICHANGED:
            case Native.WM_DISPLAYCHANGE:
                Relayout(animate: false);
                DisplayChanged?.Invoke();
                return true;

            case Native.WM_SETTINGCHANGE:
                if (wParam == (IntPtr)0x2F /* SPI_SETWORKAREA */) Relayout(animate: false);
                else if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") OnThemeChanged();
                return false;
        }
        return false;
    }

    /// <summary>Re-reads the monitor and work area (after the taskbar is hidden or shown, monitors change, ...).</summary>
    public void Reposition() => Relayout(animate: false);

    /// <summary>The same screen can be given a new handle when the display setup changes.</summary>
    public void MoveToMonitor(IntPtr monitor)
    {
        _monitor = monitor;
        Relayout(animate: false);
    }

    public override void Dispose()
    {
        s_docks.Remove(this);
        if (s_docks.Count > 0) s_docks[0].ListenForPointer(); // this window may have been the one getting it
        _panel.Dispose();
        _calendar?.Close();
        foreach (var s in _slots) s.IconSurface?.Dispose();
        _target?.Dispose();
        base.Dispose();
    }
}
