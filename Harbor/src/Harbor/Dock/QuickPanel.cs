using System.Numerics;
using System.Windows.Threading;
using Harbor.Composition;
using Harbor.Settings;
using Vortice.DirectWrite;
using Windows.UI.Composition;
using WColor = Windows.UI.Color;

namespace Harbor.Dock;

/// <summary>
/// The panel that grows out of the Harbor icon: open the app, set the volume and screen brightness, see the
/// battery and pick the wallpaper's motion.
/// </summary>
internal sealed class QuickPanel : DockFlyout, IDisposable
{
    const int MostBrightnessRows = 4;
    const float RowPitchDip = 40, FixedHeightDip = 148;
    /// <summary>With every row showing: volume, the most screens, battery. The dock's window is sized for this.</summary>
    public const float MaxHeightDip = FixedHeightDip + RowPitchDip * (MostBrightnessRows + 2);

    enum Part { None, Open, Mute, VolumeTrack, Brightness0, Brightness1, Brightness2, Brightness3, Mode0, Mode1, Mode2 }

    /// <summary>One line of the panel: glyph, then a slider or a message, then its value.</summary>
    sealed class Row
    {
        public required SpriteVisual Glyph, Value, Message;
        public required ContainerVisual GlyphHost;
        public required float CenterY;
        public Visual Track = null!;
        public SpriteVisual Thumb = null!;
        public float TrackX, TrackW;
        public string? GlyphText, ValueText;
    }

    /// <summary>A brightness slider and the screen it's for. Source is null when no screen here can be controlled.</summary>
    sealed class BrightnessRow
    {
        public required IBrightness? Source;
        public required Row Line;
        public required Box Hit;
        public int Shown = -1;
    }

    readonly SpeakerVolume _volume = new();
    readonly ScreenBrightness _builtIn = new();
    readonly ExternalScreens _externals = ExternalScreens.Shared;
    readonly List<BrightnessRow> _brightnessRows = new();
    string? _screensBuilt;
    CompositionDrawingSurface? _circle;
    bool _hasBattery;

    WallpaperMode _mode;
    Part _pressed, _hover;
    double _volumeShown = -1;
    bool _mutedShown;
    BatteryState _batteryShown;

    Box _openBox, _muteBox, _volumeRow;
    readonly Box[] _modeBoxes = new Box[3];
    Row _volumeLine = null!, _batteryLine = null!;
    SpriteVisual _batteryFill = null!, _modePill = null!, _modeHover = null!, _openHover = null!, _muteHover = null!;
    CompositionColorBrush _batteryColor = null!;
    float _batteryTrackW;
    Visual _openVisual = null!;
    readonly SpriteVisual[] _modeLabels = new SpriteVisual[3];

    public event Action? OpenApp;
    public event Action<WallpaperMode>? ModeChosen;

    public override bool IsPressed => _pressed != Part.None;
    bool IsDragging => _pressed is Part.VolumeTrack or (>= Part.Brightness0 and <= Part.Brightness3);

    public QuickPanel(CompositionHost host, ContainerVisual parent) : base(host, parent)
    {
        // Raised on an audio thread; this class only ever runs on the thread that created it.
        var ui = Dispatcher.CurrentDispatcher;
        _volume.ChangedElsewhere += () => ui.BeginInvoke(() => { if (IsOpen && _pressed != Part.VolumeTrack) ShowVolume(animate: true); });
        _externals.Changed += OnScreensChanged;
    }

    List<IBrightness> Screens()
    {
        var screens = new List<IBrightness>();
        if (_builtIn.Available) screens.Add(_builtIn);
        screens.AddRange(_externals.Screens);
        return screens.Take(MostBrightnessRows).ToList();
    }

    static string Signature(List<IBrightness> screens) => string.Join("|", screens.Select(s => s.Name));

    void OnScreensChanged()
    {
        if (!IsOpen || IsPressed) return;
        if (Signature(Screens()) != _screensBuilt)
        {
            Build(_theme!, _s, _hasBattery);
            ShowEverything(animate: false);
            Place();
        }
        else
        {
            foreach (var row in _brightnessRows) ShowBrightness(row, animate: true);
        }
    }

    float RowsTop => MathF.Round(78 * _s);
    float RowY(int index) => RowsTop + MathF.Round(RowPitchDip * _s) * index;

    void Build(DockTheme t, float scale, bool hasBattery)
    {
        _hasBattery = hasBattery;
        var screens = Screens();
        _screensBuilt = Signature(screens);
        int brightnessRows = Math.Max(1, screens.Count), rows = 1 + brightnessRows + (hasBattery ? 1 : 0);
        BuildChrome(t, scale, FixedHeightDip + RowPitchDip * rows);

        _circle?.Dispose();
        _circle = _host.CreateSurface(64, 64);
        _host.Draw(_circle, dc => SurfacePainter.DrawCircle(dc, 64, WColor.FromArgb(255, 255, 255, 255)));

        BuildHeader(t, scale);
        _volumeLine = AddSliderRow(t, scale, RowY(0), "No audio device");
        BuildBrightnessRows(t, scale, screens);
        if (hasBattery) BuildBatteryRow(t, scale, rows - 1);
        BuildVolumeHitBoxes(scale);
        BuildModeRow(t, scale, rows);

        _volumeShown = -1;
        _batteryShown = default;
    }

    void BuildHeader(DockTheme t, float s)
    {
        float icon = MathF.Round(26 * s), y = MathF.Round(14 * s);
        int iconPx = (int)MathF.Ceiling(icon * 1.5f);
        var surface = _host.CreateSurface(iconPx, iconPx);
        _host.Draw(surface, dc => SurfacePainter.DrawHarborTile(dc, iconPx));
        var iconSprite = C.CreateSpriteVisual();
        iconSprite.Brush = C.CreateSurfaceBrush(surface);
        iconSprite.Size = new Vector2(icon);
        iconSprite.Offset = new Vector3(_pad, y, 0);
        _root.Children.InsertAtTop(iconSprite);

        var title = AddText("Harbor", 14 * s, FontWeight.SemiBold, t.LabelText);
        var credit = AddText("by Yusuf Yunus", 10.5f * s, FontWeight.Normal, t.PanelDim);
        float tx = _pad + icon + 10 * s, overlap = 2 * s, ty = y + (icon - (title.Size.Y + credit.Size.Y - overlap)) / 2;
        title.Offset = new Vector3(tx, ty, 0);
        credit.Offset = new Vector3(tx, ty + title.Size.Y - overlap, 0);

        // Primary action, accent filled.
        var label = C.CreateSpriteVisual();
        SetText(label, "Open Harbor", 12.5f * s, FontWeight.SemiBold, t.AccentText);
        float bw = MathF.Ceiling(label.Size.X + 26 * s), bh = MathF.Round(30 * s);
        var button = C.CreateContainerVisual();
        button.Size = new Vector2(bw, bh);
        button.CenterPoint = new Vector3(bw / 2, bh / 2, 0);
        float bx = _w - _pad - bw, by = y + (icon - bh) / 2;
        button.Offset = new Vector3(bx, by, 0);
        button.Children.InsertAtTop(Rounded(C.CreateColorBrush(t.Accent), button.Size, bh / 2));
        _openHover = Rounded(C.CreateColorBrush(WColor.FromArgb(40, 255, 255, 255)), button.Size, bh / 2);
        _openHover.Opacity = 0;
        button.Children.InsertAtTop(_openHover);
        label.Offset = new Vector3((bw - label.Size.X) / 2, (bh - label.Size.Y) / 2, 0);
        button.Children.InsertAtTop(label);
        _root.Children.InsertAtTop(button);
        _openVisual = button;
        _openBox = new Box(bx, by, bw, bh);

        AddDivider(t, s, MathF.Round(54 * s));
    }

    float GlyphBoxSize => MathF.Round(30 * _s);
    float GlyphBoxX => _pad - MathF.Round(6 * _s);
    float TrackLeft => GlyphBoxX + GlyphBoxSize + MathF.Round(10 * _s);
    float TrackRight => _w - _pad - MathF.Round(42 * _s);

    /// <summary>The parts every row shares: a glyph at the left and a value at the right.</summary>
    Row AddRowParts(DockTheme t, float s, float cy, string message)
    {
        float d = GlyphBoxSize;
        var host = C.CreateContainerVisual();
        host.Size = new Vector2(d);
        host.CenterPoint = new Vector3(d / 2, d / 2, 0);
        host.Offset = new Vector3(GlyphBoxX, cy - d / 2, 0);
        var glyph = C.CreateSpriteVisual();
        host.Children.InsertAtTop(glyph);
        _root.Children.InsertAtTop(host);

        var value = AddText(" ", 12.5f * s, FontWeight.Normal, t.PanelDim);
        var note = AddText(message, 12.5f * s, FontWeight.Normal, t.PanelDim);
        note.Offset = new Vector3(TrackLeft, cy - note.Size.Y / 2, 0);
        note.IsVisible = false;
        return new Row { Glyph = glyph, GlyphHost = host, Value = value, Message = note, CenterY = cy };
    }

    /// <summary>With one screen the sun says it all. With more, each slider gets the screen's name above it.</summary>
    void BuildBrightnessRows(DockTheme t, float s, List<IBrightness> screens)
    {
        _brightnessRows.Clear();
        bool named = screens.Count > 1;
        float height = MathF.Round(RowPitchDip * s), left = TrackLeft - 8 * s, width = _w - left;
        for (int i = 0; i < Math.Max(1, screens.Count); i++)
        {
            float rowY = RowY(1 + i), cy = rowY + (named ? MathF.Round(6 * s) : 0);
            var line = AddSliderRow(t, s, cy, "Not supported by this screen");
            if (named)
            {
                var caption = AddText(screens[i].Name, 10.5f * s, FontWeight.Normal, t.PanelDim);
                caption.Offset = new Vector3(TrackLeft, rowY - MathF.Round(16 * s), 0);
            }
            _brightnessRows.Add(new BrightnessRow
            {
                Source = i < screens.Count ? screens[i] : null,
                Line = line,
                Hit = new Box(left, rowY - height / 2, width, height),
            });
        }
    }

    Row AddSliderRow(DockTheme t, float s, float cy, string message)
    {
        var row = AddRowParts(t, s, cy, message);
        float x = TrackLeft, w = TrackRight - TrackLeft, th = MathF.Max(2, MathF.Round(4 * s));

        var track = C.CreateContainerVisual();
        track.Size = new Vector2(w, th);
        track.Offset = new Vector3(x, row.CenterY - th / 2, 0);
        var clip = C.CreateRoundedRectangleGeometry();
        clip.Size = track.Size;
        clip.CornerRadius = new Vector2(th / 2);
        track.Clip = C.CreateGeometricClip(clip);
        var bg = C.CreateSpriteVisual();
        bg.RelativeSizeAdjustment = Vector2.One;
        bg.Brush = C.CreateColorBrush(t.PanelTrack);
        track.Children.InsertAtTop(bg);
        var fill = C.CreateSpriteVisual();
        fill.Brush = C.CreateColorBrush(t.Accent);
        fill.Size = new Vector2(0, th);
        track.Children.InsertAtTop(fill);
        _root.Children.InsertAtTop(track);

        float d = MathF.Round(16 * s);
        var thumb = C.CreateSpriteVisual();
        var brush = C.CreateSurfaceBrush(_circle);
        thumb.Brush = brush;
        thumb.Size = new Vector2(d);
        thumb.AnchorPoint = new Vector2(0.5f, 0.5f);
        thumb.CenterPoint = new Vector3(d / 2, d / 2, 0);
        var shadow = C.CreateDropShadow();
        shadow.Mask = brush;
        shadow.Opacity = 0.35f;
        shadow.BlurRadius = 5 * s;
        shadow.Offset = new Vector3(0, 1.5f * s, 0);
        thumb.Shadow = shadow;
        _root.Children.InsertAtTop(thumb);

        // The filled part simply follows the thumb, so the two can never disagree mid-animation.
        var follow = C.CreateExpressionAnimation("Vector2(Max(thumb.Offset.X - tx, 0), th)");
        follow.SetReferenceParameter("thumb", thumb);
        follow.SetScalarParameter("tx", x);
        follow.SetScalarParameter("th", th);
        fill.StartAnimation("Size", follow);

        row.Track = track;
        row.Thumb = thumb;
        row.TrackX = x;
        row.TrackW = w;
        return row;
    }

    void BuildVolumeHitBoxes(float s)
    {
        float d = GlyphBoxSize;
        _muteBox = new Box(GlyphBoxX, _volumeLine.CenterY - d / 2, d, d);
        _muteHover = Rounded(C.CreateColorBrush(_theme!.PanelHover), new Vector2(d), d / 2);
        _muteHover.Opacity = 0;
        _volumeLine.GlyphHost.Children.InsertAtBottom(_muteHover);

        float height = MathF.Round(RowPitchDip * s), left = TrackLeft - 8 * s, width = _w - left;
        _volumeRow = new Box(left, _volumeLine.CenterY - height / 2, width, height);
    }

    /// <summary>The battery is read-only, so it's a filled bar with no thumb and nothing that looks draggable.</summary>
    void BuildBatteryRow(DockTheme t, float s, int index)
    {
        _batteryLine = AddRowParts(t, s, RowY(index), "");
        float x = TrackLeft, th = MathF.Round(6 * s);
        _batteryTrackW = TrackRight - TrackLeft;
        var track = C.CreateContainerVisual();
        track.Size = new Vector2(_batteryTrackW, th);
        track.Offset = new Vector3(x, _batteryLine.CenterY - th / 2, 0);
        var clip = C.CreateRoundedRectangleGeometry();
        clip.Size = track.Size;
        clip.CornerRadius = new Vector2(th / 2);
        track.Clip = C.CreateGeometricClip(clip);
        var bg = C.CreateSpriteVisual();
        bg.RelativeSizeAdjustment = Vector2.One;
        bg.Brush = C.CreateColorBrush(t.PanelTrack);
        track.Children.InsertAtTop(bg);
        _batteryColor = C.CreateColorBrush(t.BatteryNormal);
        _batteryFill = C.CreateSpriteVisual();
        _batteryFill.Brush = _batteryColor;
        _batteryFill.Size = new Vector2(0, th);
        track.Children.InsertAtTop(_batteryFill);
        _root.Children.InsertAtTop(track);
    }

    void BuildModeRow(DockTheme t, float s, int rows)
    {
        float divider = RowY(rows - 1) + MathF.Round(24 * s);
        AddDivider(t, s, divider);
        float labelY = divider + MathF.Round(10 * s), y = labelY + MathF.Round(24 * s), h = MathF.Round(38 * s);
        var l = AddText("Wallpaper motion", 12.5f * s, FontWeight.Normal, t.PanelDim);
        l.Offset = new Vector3(_pad, labelY, 0);

        float w = _w - _pad * 2, inset = MathF.Round(3 * s), seg = (w - inset * 2) / 3;
        var track = Rounded(C.CreateColorBrush(t.PanelHover), new Vector2(w, h), MathF.Round(10 * s));
        track.Offset = new Vector3(_pad, y, 0);
        _root.Children.InsertAtTop(track);

        var pillSize = new Vector2(seg, h - inset * 2);
        _modeHover = Rounded(C.CreateColorBrush(t.PanelHover), pillSize, MathF.Round(8 * s));
        _modeHover.Opacity = 0;
        _root.Children.InsertAtTop(_modeHover);
        _modePill = Rounded(C.CreateColorBrush(t.Accent), pillSize, MathF.Round(8 * s));
        _root.Children.InsertAtTop(_modePill);

        for (int i = 0; i < 3; i++)
        {
            _modeBoxes[i] = new Box(_pad + inset + seg * i, y + inset, seg, h - inset * 2);
            _modeLabels[i] = C.CreateSpriteVisual();
            _root.Children.InsertAtTop(_modeLabels[i]);
        }
    }

    // ------------------------------------------------------------------ values

    void SetGlyph(Row row, string glyph)
    {
        if (row.GlyphText == glyph) return;
        row.GlyphText = glyph;
        SetText(row.Glyph, glyph, 16 * _s, FontWeight.Normal, _theme!.LabelText, UiFonts.Icons);
        float d = GlyphBoxSize;
        row.Glyph.Offset = new Vector3((d - row.Glyph.Size.X) / 2, (d - row.Glyph.Size.Y) / 2, 0);
    }

    void SetValue(Row row, string text)
    {
        if (row.ValueText == text) return;
        row.ValueText = text;
        SetText(row.Value, text, 12.5f * _s, FontWeight.Normal, _theme!.PanelDim);
        row.Value.Offset = new Vector3(_w - _pad - row.Value.Size.X, row.CenterY - row.Value.Size.Y / 2, 0);
    }

    void SetAvailable(Row row, bool available)
    {
        row.Track.IsVisible = row.Thumb.IsVisible = row.Value.IsVisible = available;
        row.Message.IsVisible = !available;
    }

    void SetThumb(Row row, float f, bool animate)
    {
        var target = new Vector3(row.TrackX + Math.Clamp(f, 0, 1) * row.TrackW, row.CenterY, 0);
        if (animate)
        {
            var a = C.CreateVector3KeyFrameAnimation();
            a.InsertExpressionKeyFrame(0, "this.StartingValue");
            a.InsertKeyFrame(1, target, _easeOut);
            a.Duration = TimeSpan.FromMilliseconds(140);
            row.Thumb.StartAnimation("Offset", a);
        }
        else
        {
            row.Thumb.StopAnimation("Offset");
            row.Thumb.Offset = target;
        }
    }

    static string VolumeGlyph(double level, bool muted) =>
        muted ? "\uE74F" : level <= 0.001 ? "\uE992" : level < 0.34 ? "\uE993" : level < 0.67 ? "\uE994" : "\uE995";

    /// <summary>Matches the Windows slider, which shows the same 0 to 100 scale.</summary>
    void ShowVolume(bool animate)
    {
        SetAvailable(_volumeLine, _volume.Available);
        SetGlyph(_volumeLine, VolumeGlyph(_volume.Level, _volume.Muted));
        if (!_volume.Available) return;
        double level = _volume.Level;
        bool muted = _volume.Muted;
        if (level == _volumeShown && muted == _mutedShown) return;
        _volumeShown = level;
        _mutedShown = muted;
        SetValue(_volumeLine, muted ? "Muted" : $"{Math.Round(level * 100):0}%");
        SetThumb(_volumeLine, (float)level, animate);
    }

    void ShowBrightness(BrightnessRow row, bool animate)
    {
        SetAvailable(row.Line, row.Source != null);
        SetGlyph(row.Line, "\uE706");
        if (row.Source == null) return;
        int value = row.Source.Read();
        if (value == row.Shown) return;
        row.Shown = value;
        SetValue(row.Line, $"{value}%");
        int span = Math.Max(1, row.Source.Maximum - row.Source.Minimum);
        SetThumb(row.Line, (value - row.Source.Minimum) / (float)span, animate);
    }

    void ShowEverything(bool animate)
    {
        ShowVolume(animate);
        foreach (var row in _brightnessRows) ShowBrightness(row, animate);
        ShowBattery(BatteryState.Read());
        ShowMode(animate);
    }

    void ShowBattery(BatteryState b)
    {
        if (!_hasBattery || b == _batteryShown) return;
        _batteryShown = b;
        int step = Math.Clamp((int)Math.Round(b.Percent / 10.0), 0, 10);
        // Segoe Fluent Icons keeps its battery levels in two runs, with the last ones stored elsewhere.
        string glyph = b.Plugged
            ? step == 10 ? "\uEA93" : step == 9 ? "\uE83E" : ((char)(0xE85A + step)).ToString()
            : step == 10 ? "\uE83F" : ((char)(0xE850 + step)).ToString();
        SetGlyph(_batteryLine, glyph);
        SetValue(_batteryLine, $"{b.Percent}%");
        var t = _theme!;
        _batteryColor.Color = b.Plugged ? t.BatteryCharging : b.Percent <= 20 ? t.BatteryLow : b.Saver ? t.BatterySaver : t.BatteryNormal;
        _batteryFill.Size = new Vector2(_batteryTrackW * Math.Clamp(b.Percent / 100f, 0, 1), _batteryFill.Size.Y);
    }

    void ShowMode(bool animate)
    {
        var t = _theme!;
        string[] names = { "Smart", "Animated", "Static" };
        int selected = (int)_mode;
        for (int i = 0; i < 3; i++)
        {
            SetText(_modeLabels[i], names[i], 12.5f * _s, i == selected ? FontWeight.SemiBold : FontWeight.Normal, i == selected ? t.AccentText : t.LabelText);
            var b = _modeBoxes[i];
            _modeLabels[i].Offset = new Vector3(b.X + (b.W - _modeLabels[i].Size.X) / 2, b.Y + (b.H - _modeLabels[i].Size.Y) / 2, 0);
        }
        var target = new Vector3(_modeBoxes[selected].X, _modeBoxes[selected].Y, 0);
        if (animate)
        {
            // A spring keeps its velocity if you change your mind halfway across.
            var a = C.CreateSpringVector3Animation();
            a.FinalValue = target;
            a.Period = TimeSpan.FromMilliseconds(42);
            a.DampingRatio = 0.9f;
            _modePill.StartAnimation("Offset", a);
        }
        else
        {
            _modePill.StopAnimation("Offset");
            _modePill.Offset = target;
        }
    }

    /// <summary>The wallpaper mode changed somewhere else.</summary>
    public void SyncMode(WallpaperMode mode)
    {
        bool changed = mode != _mode;
        _mode = mode;
        if (_built && changed) ShowMode(IsOpen);
    }

    /// <summary>Called about once a second while open: the battery drains, and brightness keys work behind the panel.</summary>
    public override void Refresh()
    {
        if (!IsOpen) return;
        ShowBattery(BatteryState.Read());
        for (int i = 0; i < _brightnessRows.Count; i++)
            if (_pressed != Part.Brightness0 + i) ShowBrightness(_brightnessRows[i], animate: true);
    }

    // ------------------------------------------------------------------ open / close

    public void Open(DockTheme theme, float scale, WallpaperMode mode, float anchorX, float bottom, float windowWidth)
    {
        _mode = mode;
        var battery = BatteryState.Read();
        _builtIn.Open();
        _externals.Open();
        if (!_built || scale != _s || !ReferenceEquals(theme, _theme) || battery.Present != _hasBattery || Signature(Screens()) != _screensBuilt)
            Build(theme, scale, battery.Present);
        _volume.Open();
        ShowEverything(animate: false);

        _hover = Part.None;
        Present(anchorX, bottom, windowWidth);
    }

    public override void Close()
    {
        if (!IsOpen) return;
        _pressed = Part.None;
        _hover = Part.None;
        _volume.Dispose();
        _builtIn.Dispose();
        _externals.Release();
        base.Close();
    }

    public void Dispose()
    {
        Close();
        _externals.Changed -= OnScreensChanged;
    }

    // ------------------------------------------------------------------ input (window pixels)

    Part PartAt(float lx, float ly)
    {
        if (_openBox.Contains(lx, ly)) return Part.Open;
        if (_muteBox.Contains(lx, ly) && _volume.Available) return Part.Mute;
        if (_volumeRow.Contains(lx, ly) && _volume.Available) return Part.VolumeTrack;
        for (int i = 0; i < _brightnessRows.Count; i++)
            if (_brightnessRows[i].Source != null && _brightnessRows[i].Hit.Contains(lx, ly)) return Part.Brightness0 + i;
        for (int i = 0; i < 3; i++) if (_modeBoxes[i].Contains(lx, ly)) return Part.Mode0 + i;
        return Part.None;
    }

    void SetHover(Part part)
    {
        if (part == _hover) return;
        Hovered(_hover, false);
        _hover = part;
        Hovered(part, true);
    }

    void Hovered(Part part, bool on)
    {
        switch (part)
        {
            case Part.Open: Fade(_openHover, on ? 1 : 0); break;
            case Part.Mute: Fade(_muteHover, on ? 1 : 0); break;
            case Part.VolumeTrack: ScaleTo(_volumeLine.Thumb, on ? 1.15f : 1, 120); break;
            case >= Part.Brightness0 and <= Part.Brightness3:
                if (part - Part.Brightness0 < _brightnessRows.Count) ScaleTo(_brightnessRows[part - Part.Brightness0].Line.Thumb, on ? 1.15f : 1, 120);
                break;
            case Part.Mode0 or Part.Mode1 or Part.Mode2:
                int i = part - Part.Mode0;
                if (on && i != (int)_mode) _modeHover.Offset = new Vector3(_modeBoxes[i].X, _modeBoxes[i].Y, 0);
                Fade(_modeHover, on && i != (int)_mode ? 1 : 0);
                break;
        }
    }

    public override void OnMove(float x, float y)
    {
        if (!IsOpen) return;
        float lx = x - _left, ly = y - _top;
        if (IsDragging) { DragTo(lx); return; }
        if (!IsPressed) SetHover(x >= _left && x < _left + _w && y >= _top && y < _top + _h ? PartAt(lx, ly) : Part.None);
    }

    public override void OnDown(float x, float y)
    {
        float lx = x - _left, ly = y - _top;
        _pressed = PartAt(lx, ly);
        switch (_pressed)
        {
            case Part.Open: ScaleTo(_openVisual, 0.97f, 100); break;
            case Part.Mute: ScaleTo(_volumeLine.GlyphHost, 0.9f, 100); break;
            case Part.VolumeTrack:
            case >= Part.Brightness0 and <= Part.Brightness3: DragTo(lx); break;
        }
    }

    public override void OnUp(float x, float y)
    {
        var part = _pressed;
        _pressed = Part.None;
        float lx = x - _left, ly = y - _top;
        switch (part)
        {
            case Part.Open:
                ScaleTo(_openVisual, 1, 160);
                if (_openBox.Contains(lx, ly)) OpenApp?.Invoke();
                break;
            case Part.Mute:
                ScaleTo(_volumeLine.GlyphHost, 1, 160);
                if (_muteBox.Contains(lx, ly)) { _volume.ToggleMute(); ShowVolume(animate: false); }
                break;
            case Part.Mode0 or Part.Mode1 or Part.Mode2:
                int i = part - Part.Mode0;
                if (_modeBoxes[i].Contains(lx, ly) && (WallpaperMode)i != _mode)
                {
                    _mode = (WallpaperMode)i;
                    Fade(_modeHover, 0);
                    ShowMode(true);
                    ModeChosen?.Invoke(_mode);
                }
                break;
        }
    }

    public override void OnWheel(float x, float y, int direction)
    {
        float lx = x - _left, ly = y - _top;
        if (_volumeRow.Contains(lx, ly) && _volume.Available)
        {
            _volume.Set(_volume.Level + direction * 0.02f);
            ShowVolume(animate: true);
        }
        else
        {
            foreach (var row in _brightnessRows)
            {
                if (row.Source == null || !row.Hit.Contains(lx, ly)) continue;
                row.Source.Set(row.Source.Read() + direction * 2);
                ShowBrightness(row, animate: true);
            }
        }
    }

    void DragTo(float lx)
    {
        if (_pressed == Part.VolumeTrack)
        {
            _volume.Set(Math.Clamp((lx - _volumeLine.TrackX) / _volumeLine.TrackW, 0, 1));
            ShowVolume(animate: false);
        }
        else
        {
            var row = _brightnessRows[_pressed - Part.Brightness0];
            float f = Math.Clamp((lx - row.Line.TrackX) / row.Line.TrackW, 0, 1);
            row.Source!.Set(row.Source.Minimum + (int)Math.Round(f * (row.Source.Maximum - row.Source.Minimum)));
            ShowBrightness(row, animate: false);
        }
    }
}
