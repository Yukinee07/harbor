using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Harbor.Interop;
using Harbor.Pomodoro;
using Harbor.Settings;

namespace Harbor.UI;

/// <summary>
/// The floating timer. At rest it's only a thin bar showing how much time is left; under the pointer it opens into the full
/// timer. The window stays the size of the open timer the whole time and its empty part lets clicks through, so nothing
/// has to be moved or resized as it opens.
/// </summary>
public partial class PomodoroWindow : Window
{
    const double MiniWidth = 168, MiniHeight = 14, FullWidth = 280, FullHeight = 158, MiniBar = 158, FullBar = 244;

    readonly PomodoroTimer _timer;
    readonly PomodoroSettings _settings;
    readonly Action _save, _close;
    readonly DispatcherTimer _hover = new();
    bool _expanded, _over, _dragging;
    long _holdOpenUntil;

    internal PomodoroWindow(PomodoroTimer timer, PomodoroSettings settings, Action save, Action close)
    {
        _timer = timer;
        _settings = settings;
        _save = save;
        _close = close;
        InitializeComponent();

        for (int i = 0; i < PomodoroTimer.RoundsPerSet; i++)
            Dots.Children.Add(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 4, 0) });

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Clicking its buttons shouldn't take the keyboard away from whatever you're working in.
            Native.SetExStyle(hwnd, Native.GetExStyle(hwnd) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Place();
        };
        Loaded += (_, _) => Place(); // landing on a differently scaled screen resizes the window
        _hover.Tick += (_, _) => { _hover.Stop(); Settle(); };
        _timer.Changed += Refresh;
        _timer.Ticked += Refresh;
        Closed += (_, _) =>
        {
            _timer.Changed -= Refresh;
            _timer.Ticked -= Refresh;
            _hover.Stop();
        };
        Refresh();
    }

    internal Native.RECT ScreenRect
    {
        get
        {
            Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
            return r;
        }
    }

    // ------------------------------------------------------------------ what it shows

    void Refresh()
    {
        var left = _timer.Remaining;
        int seconds = (int)Math.Ceiling(left.TotalSeconds);
        TimeText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        PhaseText.Text = _timer.Phase switch { PomodoroPhase.Focus => "Focus", PomodoroPhase.ShortBreak => "Break", _ => "Long break" };

        string colour = _timer.OnBreak ? "H.StatusAnimated" : "H.Accent";
        MiniFill.SetResourceReference(BackgroundProperty, colour);
        FullFill.SetResourceReference(BackgroundProperty, colour);
        MiniFill.Width = Math.Round(MiniBar * _timer.Fraction);
        FullFill.Width = Math.Round(FullBar * _timer.Fraction);
        MiniFill.Opacity = _timer.Running ? 1 : 0.55;

        PlayButton.Content = _timer.Running ? "" : "";
        PlayButton.ToolTip = _timer.Running ? "Pause" : "Start";
        for (int i = 0; i < Dots.Children.Count; i++)
            ((Ellipse)Dots.Children[i]).SetResourceReference(Shape.FillProperty, i < _timer.Rounds ? "H.Accent" : "H.Track");
    }

    // ------------------------------------------------------------------ opening and closing under the pointer

    void Zone_MouseEnter(object sender, MouseEventArgs e)
    {
        _over = true;
        SettleAfter(90);
    }

    void Zone_MouseLeave(object sender, MouseEventArgs e)
    {
        _over = false;
        SettleAfter(420);
    }

    void SettleAfter(double ms)
    {
        _hover.Stop();
        _hover.Interval = TimeSpan.FromMilliseconds(ms);
        _hover.Start();
    }

    void Settle()
    {
        long wait = _holdOpenUntil - Environment.TickCount64;
        if (wait > 0) SettleAfter(wait + 20);
        SetExpanded(_over || _dragging || wait > 0);
    }

    /// <summary>Opens for a moment by itself, when a phase ends.</summary>
    internal void ShowFullFor(int ms)
    {
        _holdOpenUntil = Environment.TickCount64 + ms;
        Settle();
    }

    void SetExpanded(bool expanded)
    {
        if (expanded == _expanded) return;
        _expanded = expanded;
        Full.IsHitTestVisible = expanded;
        Slide(Card, WidthProperty, expanded ? FullWidth : MiniWidth, 200);
        Slide(Card, HeightProperty, expanded ? FullHeight : MiniHeight, 200);
        Slide(Full, OpacityProperty, expanded ? 1 : 0, expanded ? 160 : 90);
        Slide(Mini, OpacityProperty, expanded ? 0 : 1, expanded ? 90 : 160);
    }

    static void Slide(UIElement element, DependencyProperty property, double to, double ms)
    {
        var duration = ThemeManager.AnimationsEnabled ? ms : 0;
        element.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ------------------------------------------------------------------ moving it

    void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { } // the button was already up
        _dragging = false;
        var r = ScreenRect;
        Keep(r.Left, r.Top);
        _settings.X = ScreenRect.Left;
        _settings.Y = ScreenRect.Top;
        _save();
        Settle();
    }

    /// <summary>Where it was left, or the top middle of the main screen the first time. In real pixels, as screens can be scaled differently.</summary>
    void Place()
    {
        var r = ScreenRect;
        if (_settings.X is { } x && _settings.Y is { } y)
        {
            Keep(x, y);
            return;
        }
        var work = Native.GetMonitorInfo(Native.PrimaryMonitor()).rcWork;
        Keep(work.Left + (work.Width - r.Width) / 2, work.Top);
    }

    /// <summary>Moves it there, fully on a screen, and has it open away from the nearest screen edge.</summary>
    void Keep(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var r = ScreenRect;
        var work = Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT { X = x + r.Width / 2, Y = y + r.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST)).rcWork;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - r.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - r.Height));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Zone.VerticalAlignment = y + r.Height / 2 > (work.Top + work.Bottom) / 2 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    // ------------------------------------------------------------------ buttons

    void Play_Click(object sender, RoutedEventArgs e) => _timer.Toggle();
    void Reset_Click(object sender, RoutedEventArgs e) => _timer.Reset();
    void Skip_Click(object sender, RoutedEventArgs e) => _timer.Skip();
    void Close_Click(object sender, RoutedEventArgs e) => _close();
}
