using System.Globalization;
using System.Numerics;
using Harbor.Composition;
using Vortice.DirectWrite;
using Windows.UI.Composition;

namespace Harbor.Dock;

/// <summary>The panel that grows out of the clock: the time to the second, today's date and a month you can page through.</summary>
internal sealed class CalendarPanel : DockFlyout
{
    public const float HeightDip = 362;
    const int Weeks = 6, Cells = Weeks * 7;
    const float CellHeightDip = 34;

    enum Part { None, Previous, Next, Today }
    enum DayKind { ThisMonth, OtherMonth, Today }

    SpriteVisual _time = null!, _date = null!, _monthTitle = null!, _todayMark = null!;
    SpriteVisual _previousHover = null!, _nextHover = null!, _todayHover = null!;
    Visual _previousButton = null!, _nextButton = null!, _todayButton = null!;
    readonly SpriteVisual[] _days = new SpriteVisual[Cells];
    readonly Dictionary<(int Day, DayKind Kind), (CompositionSurfaceBrush Brush, Vector2 Size)> _dayText = new();

    Box _previousBox, _nextBox, _todayBox;
    float _gridLeft, _gridTop, _cellW, _cellH;
    DateTime _month, _lastToday;
    string? _timeShown;
    Part _pressed, _hover;

    public override bool IsPressed => _pressed != Part.None;

    public CalendarPanel(CompositionHost host, ContainerVisual parent) : base(host, parent) { }

    static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    // ------------------------------------------------------------------ building

    void Build(DockTheme t, float s)
    {
        foreach (var (brush, _) in _dayText.Values) { (brush.Surface as IDisposable)?.Dispose(); brush.Dispose(); }
        _dayText.Clear();
        BuildChrome(t, s, HeightDip);

        _time = AddText(" ", 30 * s, FontWeight.SemiBold, t.LabelText, UiFonts.Display);
        _date = AddText(" ", 12.5f * s, FontWeight.Normal, t.PanelDim);
        _timeShown = null;
        AddDivider(t, s, MathF.Round(80 * s));

        float monthY = MathF.Round(90 * s), button = MathF.Round(28 * s);
        _monthTitle = AddText(" ", 14 * s, FontWeight.SemiBold, t.LabelText);
        _monthTitle.Offset = new Vector3(_pad, monthY + MathF.Round(4 * s), 0);
        (_nextButton, _nextHover, _nextBox) = AddChevron("", t, s, _w - _pad - button + MathF.Round(6 * s), monthY, button);
        (_previousButton, _previousHover, _previousBox) = AddChevron("", t, s, _nextBox.X - button, monthY, button);
        BuildTodayButton(t, s, monthY, button);

        _cellW = (_w - _pad * 2) / 7;
        _cellH = MathF.Round(CellHeightDip * s);
        _gridLeft = _pad;
        _gridTop = MathF.Round(146 * s);

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            string name = format.GetShortestDayName((DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7));
            var label = AddText(name, 11 * s, FontWeight.Normal, t.PanelDim);
            label.Offset = new Vector3(_gridLeft + _cellW * (i + 0.5f) - label.Size.X / 2, MathF.Round(124 * s), 0);
        }

        float mark = MathF.Round(28 * s);
        _todayMark = Rounded(C.CreateColorBrush(t.Accent), new Vector2(mark), mark / 2);
        _root.Children.InsertAtTop(_todayMark);
        for (int i = 0; i < Cells; i++)
        {
            _days[i] = C.CreateSpriteVisual();
            _root.Children.InsertAtTop(_days[i]);
        }
    }

    (Visual, SpriteVisual, Box) AddChevron(string glyph, DockTheme t, float s, float x, float y, float d)
    {
        var host = C.CreateContainerVisual();
        host.Size = new Vector2(d);
        host.CenterPoint = new Vector3(d / 2, d / 2, 0);
        host.Offset = new Vector3(x, y, 0);
        var hover = Rounded(C.CreateColorBrush(t.PanelHover), host.Size, d / 2);
        hover.Opacity = 0;
        host.Children.InsertAtTop(hover);
        var g = C.CreateSpriteVisual();
        SetText(g, glyph, 11 * s, FontWeight.Normal, t.LabelText, UiFonts.Icons);
        g.Offset = new Vector3((d - g.Size.X) / 2, (d - g.Size.Y) / 2, 0);
        host.Children.InsertAtTop(g);
        _root.Children.InsertAtTop(host);
        return (host, hover, new Box(x, y, d, d));
    }

    void BuildTodayButton(DockTheme t, float s, float y, float height)
    {
        var label = C.CreateSpriteVisual();
        SetText(label, "Today", 12 * s, FontWeight.Normal, t.LabelText);
        float w = MathF.Ceiling(label.Size.X + 18 * s);
        float x = _previousBox.X - w - MathF.Round(2 * s);
        var host = C.CreateContainerVisual();
        host.Size = new Vector2(w, height);
        host.CenterPoint = new Vector3(w / 2, height / 2, 0);
        host.Offset = new Vector3(x, y, 0);
        _todayHover = Rounded(C.CreateColorBrush(t.PanelHover), host.Size, height / 2);
        _todayHover.Opacity = 0;
        host.Children.InsertAtTop(_todayHover);
        label.Offset = new Vector3((w - label.Size.X) / 2, (height - label.Size.Y) / 2, 0);
        host.Children.InsertAtTop(label);
        _root.Children.InsertAtTop(host);
        _todayButton = host;
        _todayBox = new Box(x, y, w, height);
    }

    // ------------------------------------------------------------------ values

    void ShowTime()
    {
        var now = DateTime.Now;
        string time = now.ToString("T");
        if (time != _timeShown)
        {
            _timeShown = time;
            SetText(_time, time, 30 * _s, FontWeight.SemiBold, _theme!.LabelText, UiFonts.Display);
            _time.Offset = new Vector3(_pad, MathF.Round(10 * _s), 0);
        }
        if (now.Date != _lastToday)
        {
            _lastToday = now.Date;
            SetText(_date, now.ToString("D"), 12.5f * _s, FontWeight.Normal, _theme!.PanelDim);
            _date.Offset = new Vector3(_pad, MathF.Round(52 * _s), 0);
        }
    }

    (CompositionSurfaceBrush Brush, Vector2 Size) DayText(int day, DayKind kind)
    {
        if (_dayText.TryGetValue((day, kind), out var cached)) return cached;
        var t = _theme!;
        var (surface, size) = kind switch
        {
            DayKind.Today => DrawText(day.ToString(), 13 * _s, FontWeight.SemiBold, t.AccentText),
            DayKind.OtherMonth => DrawText(day.ToString(), 13 * _s, FontWeight.Normal, t.PanelFaint),
            _ => DrawText(day.ToString(), 13 * _s, FontWeight.Normal, t.LabelText),
        };
        var entry = (C.CreateSurfaceBrush(surface), size);
        _dayText[(day, kind)] = entry;
        return entry;
    }

    void ShowMonth()
    {
        SetText(_monthTitle, _month.ToString("MMMM yyyy"), 14 * _s, FontWeight.SemiBold, _theme!.LabelText);

        // Back up to the start of the week the 1st falls in.
        int firstDay = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int lead = ((int)_month.DayOfWeek - firstDay + 7) % 7;
        var today = DateTime.Today;
        _todayMark.IsVisible = false;
        for (int i = 0; i < Cells; i++)
        {
            var date = _month.AddDays(i - lead);
            var kind = date == today ? DayKind.Today : date.Month == _month.Month ? DayKind.ThisMonth : DayKind.OtherMonth;
            var (brush, size) = DayText(date.Day, kind);
            float cx = _gridLeft + _cellW * (i % 7 + 0.5f), cy = _gridTop + _cellH * (i / 7 + 0.5f);
            _days[i].Brush = brush;
            _days[i].Size = size;
            _days[i].Offset = new Vector3(MathF.Round(cx - size.X / 2), MathF.Round(cy - size.Y / 2), 0);
            if (kind == DayKind.Today)
            {
                _todayMark.Offset = new Vector3(MathF.Round(cx - _todayMark.Size.X / 2), MathF.Round(cy - _todayMark.Size.Y / 2), 0);
                _todayMark.IsVisible = true;
            }
        }
        _todayButton.IsVisible = _month != FirstOfMonth(today);
    }

    void Step(int months)
    {
        _month = _month.AddMonths(months);
        ShowMonth();
    }

    public override void Refresh()
    {
        if (!IsOpen) return;
        var today = _lastToday;
        ShowTime();
        if (_lastToday != today) ShowMonth(); // it just turned midnight
    }

    // ------------------------------------------------------------------ open / close

    public void Open(DockTheme theme, float scale, float anchorX, float bottom, float windowWidth)
    {
        if (!_built || scale != _s || !ReferenceEquals(theme, _theme))
        {
            Build(theme, scale);
            _lastToday = default;
        }
        _month = FirstOfMonth(DateTime.Today);
        ShowTime();
        ShowMonth();
        _hover = Part.None;
        Present(anchorX, bottom, windowWidth);
    }

    public override void Close()
    {
        _pressed = Part.None;
        _hover = Part.None;
        base.Close();
    }

    // ------------------------------------------------------------------ input

    Part PartAt(float lx, float ly)
    {
        if (_previousBox.Contains(lx, ly)) return Part.Previous;
        if (_nextBox.Contains(lx, ly)) return Part.Next;
        if (_todayButton.IsVisible && _todayBox.Contains(lx, ly)) return Part.Today;
        return Part.None;
    }

    SpriteVisual? HoverOf(Part part) => part switch
    {
        Part.Previous => _previousHover,
        Part.Next => _nextHover,
        Part.Today => _todayHover,
        _ => null,
    };

    Visual? ButtonOf(Part part) => part switch
    {
        Part.Previous => _previousButton,
        Part.Next => _nextButton,
        Part.Today => _todayButton,
        _ => null,
    };

    public override void OnMove(float x, float y)
    {
        if (!IsOpen || IsPressed) return;
        var part = Contains(x, y) ? PartAt(x - _left, y - _top) : Part.None;
        if (part == _hover) return;
        if (HoverOf(_hover) is { } was) Fade(was, 0);
        _hover = part;
        if (HoverOf(part) is { } now) Fade(now, 1);
    }

    public override void OnDown(float x, float y)
    {
        _pressed = PartAt(x - _left, y - _top);
        if (ButtonOf(_pressed) is { } button) ScaleTo(button, 0.92f, 100);
    }

    public override void OnUp(float x, float y)
    {
        var part = _pressed;
        _pressed = Part.None;
        if (ButtonOf(part) is { } button) ScaleTo(button, 1, 160);
        if (part == Part.None || PartAt(x - _left, y - _top) != part) return;
        switch (part)
        {
            case Part.Previous: Step(-1); break;
            case Part.Next: Step(+1); break;
            case Part.Today:
                _month = FirstOfMonth(DateTime.Today);
                ShowMonth();
                break;
        }
    }

    public override void OnWheel(float x, float y, int direction) => Step(direction > 0 ? -1 : 1);
}
