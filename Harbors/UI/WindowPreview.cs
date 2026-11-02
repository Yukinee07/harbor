using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Harbor.Dock;
using Harbor.Interop;

namespace Harbor.UI;

/// <summary>
/// Live thumbnails of an app's windows above its dock icon. The pictures are DWM thumbnails, so they are
/// the real, live window contents composited by Windows at no cost to us. Never takes focus.
/// </summary>
internal sealed class WindowPreview : Window
{
    [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public Native.RECT rcDestination, rcSource;
        public byte opacity;
        public bool fVisible, fSourceClientAreaOnly;
    }
    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);

    sealed class Tile
    {
        public IntPtr Window;
        public IntPtr Thumb;
        public required Border Frame;
        public required Border Slot;
        public required StackPanel Stack;
        public required TextBlock Title;
        public required Button Close;
    }

    const double ScreenEdge = 8, RowMargin = 6, CloseWidth = 18, CloseAtRest = 0.55;
    double ThumbW, ThumbH;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(RowMargin) };
    readonly List<Tile> _tiles = new();
    IntPtr _hwnd;
    Native.RECT _anchor;
    bool _reflowWhenPointerLeaves;

    public DockItem? Item { get; private set; }
    public event Action<IntPtr>? WindowChosen;

    public WindowPreview()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Background = Brushes.Transparent;
        FontFamily = (FontFamily)FindResource("H.Font");
        WindowChrome.SetWindowChrome(this, new WindowChrome { GlassFrameThickness = new Thickness(-1), CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Content = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("H.MenuBg"),
            Opacity = 0.98,
            Child = _row,
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(_hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.SetExStyle(_hwnd, Native.GetExStyle(_hwnd) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            Native.SetDwmInt(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
            Native.SetDwmInt(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ThemeManager.IsDark ? 1 : 0);
            Native.SetDwmInt(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3); // transient acrylic
        };
        MouseLeave += (_, _) =>
        {
            if (!_reflowWhenPointerLeaves || _tiles.Count == 0) return;
            _reflowWhenPointerLeaves = false;
            Arrange();
        };
    }

    /// <summary>Shows (or switches to) the windows of <paramref name="item"/>, centred above <paramref name="anchor"/> (physical px).</summary>
    public void ShowFor(DockItem item, Native.RECT anchor)
    {
        Item = item;
        _anchor = anchor;
        _reflowWhenPointerLeaves = false;
        ClearTiles();
        double available = AvailableWidth();
        foreach (var hwnd in item.Windows.Where(Native.IsWindow).Take(TileSize.MostThatFit(available))) AddTile(hwnd);
        if (_tiles.Count == 0) { Hide(); return; }
        if (!IsVisible) { Left = -10000; Top = -10000; Show(); }
        Arrange();
    }

    double AvailableWidth()
    {
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = _anchor.Left, Y = _anchor.Top }, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetMonitorInfo(monitor).rcMonitor.Width / Native.GetMonitorScale(monitor) - (ScreenEdge + RowMargin) * 2;
    }

    /// <summary>Sizes every tile so they share one line, and centres the row over the icon. Also runs when a window is closed from here.</summary>
    void Arrange()
    {
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = _anchor.Left, Y = _anchor.Top }, Native.MONITOR_DEFAULTTONEAREST);
        double scale = Native.GetMonitorScale(monitor);
        // Cross to the right screen first: WPF rescales a window as it lands on a screen with different scaling, and
        // everything below is measured in that screen's units.
        if (Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST) != monitor)
            Native.SetWindowPos(_hwnd, IntPtr.Zero, _anchor.Left, _anchor.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        var size = TileSize.Fit(_tiles.Count, AvailableWidth(), scale);
        ThumbW = size.ThumbW;
        ThumbH = size.ThumbH;
        foreach (var t in _tiles)
        {
            t.Frame.Padding = new Thickness(size.Padding);
            t.Stack.Width = ThumbW;
            t.Slot.Width = ThumbW;
            t.Slot.Height = ThumbH;
            t.Title.Visibility = size.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            t.Close.Width = Math.Min(CloseWidth, ThumbW);
        }

        UpdateLayout();
        double w = ActualWidth * scale, h = ActualHeight * scale;
        var mi = Native.GetMonitorInfo(monitor);
        double edge = ScreenEdge * scale, left = (_anchor.Left + _anchor.Right) / 2.0 - w / 2;
        left = Math.Clamp(left, mi.rcMonitor.Left + edge, Math.Max(mi.rcMonitor.Left + edge, mi.rcMonitor.Right - w - edge));
        // In real pixels, because Left and Top are measured by whichever screen the window was last on. Also keeps it
        // above the dock's window, which is tall and may have been raised since; the preview must be on top to get clicks.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, (int)left, (int)(_anchor.Top - h - 10 * scale), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Dispatcher.BeginInvoke(PlaceThumbnails, System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>For the developer test: what the last layout came to.</summary>
    public string Describe()
    {
        var r = ScreenRect;
        return $"{_tiles.Count} tiles, picture {ThumbW:0.#}x{ThumbH:0.#}, on screen {r.Left},{r.Top} to {r.Right},{r.Bottom}, scale {VisualTreeHelper.GetDpi(this).DpiScaleX}";
    }

    void AddTile(IntPtr hwnd)
    {
        string text = Native.GetWindowTitle(hwnd);
        var title = new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("H.Text"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "",
            Style = (Style)FindResource("H.TileClose"),
            Opacity = CloseAtRest,
            ToolTip = "Close window",
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close " + text);
        // Windows draws the live pictures over everything in this window, so the X needs a strip of its own above the
        // picture. Every tile has one at every size, and only the title gives way when tiles get small.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 3), Height = CloseWidth };
        DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);
        var slot = new Border { CornerRadius = new CornerRadius(5), Background = (Brush)FindResource("H.Hover") };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(slot);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = text, // the only way to read it once the tiles are too small for titles
        };
        ToolTipService.SetInitialShowDelay(frame, 250);
        var tile = new Tile { Window = hwnd, Frame = frame, Slot = slot, Stack = stack, Title = title, Close = close };
        frame.MouseEnter += (_, _) => { frame.Background = (Brush)FindResource("H.Hover"); close.Opacity = 1; };
        frame.MouseLeave += (_, _) => { frame.Background = Brushes.Transparent; close.Opacity = CloseAtRest; };
        frame.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not FrameworkElement fe || fe.TemplatedParent != close) WindowChosen?.Invoke(hwnd); };
        frame.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseWindow(tile); };
        close.Click += (_, _) => CloseWindow(tile);
        _row.Children.Add(frame);
        _tiles.Add(tile);
    }

    void CloseWindow(Tile tile)
    {
        WindowActions.Close(tile.Window);
        if (tile.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(tile.Thumb);
        _row.Children.Remove(tile.Frame);
        _tiles.Remove(tile);
        if (_tiles.Count == 0)
        {
            Hide();
        }
        else if (IsMouseOver)
        {
            // Like closing browser tabs: the others keep their size and just close the gap, so the next X lands under
            // the pointer and several can be closed in a row. They grow back into the room once the pointer leaves.
            _reflowWhenPointerLeaves = true;
            UpdateLayout();
            Dispatcher.BeginInvoke(PlaceThumbnails, System.Windows.Threading.DispatcherPriority.Render);
        }
        else
        {
            Arrange();
        }
    }

    /// <summary>Registers and positions the DWM thumbnails inside their slots (physical pixels, client coordinates).</summary>
    void PlaceThumbnails()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var t in _tiles)
        {
            if (t.Thumb == IntPtr.Zero && DwmRegisterThumbnail(_hwnd, t.Window, out var thumb) == 0) t.Thumb = thumb;
            if (t.Thumb == IntPtr.Zero) continue;
            var topLeft = t.Slot.TransformToAncestor(this).Transform(new Point(0, 0));
            double sw = ThumbW, sh = ThumbH;
            if (DwmQueryThumbnailSourceSize(t.Thumb, out var src) == 0 && src.x > 0 && src.y > 0)
            {
                // Fit inside the slot, keeping the window's aspect ratio.
                double k = Math.Min(ThumbW / src.x, ThumbH / src.y);
                sw = src.x * k;
                sh = src.y * k;
            }
            double x = topLeft.X + (ThumbW - sw) / 2, y = topLeft.Y + (ThumbH - sh) / 2;
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8 | 0x10, // destination, opacity, visible, client area only
                rcDestination = new Native.RECT((int)(x * dpi.DpiScaleX), (int)(y * dpi.DpiScaleY), (int)((x + sw) * dpi.DpiScaleX), (int)((y + sh) * dpi.DpiScaleY)),
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(t.Thumb, ref props);
        }
    }

    void ClearTiles()
    {
        foreach (var t in _tiles) if (t.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(t.Thumb);
        _tiles.Clear();
        _row.Children.Clear();
    }

    public void Dismiss()
    {
        ClearTiles();
        Item = null;
        Hide();
    }

    /// <summary>Screen rectangle in physical pixels, for hover tracking.</summary>
    public Native.RECT ScreenRect
    {
        get
        {
            Native.GetWindowRect(_hwnd, out var r);
            return r;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearTiles();
        base.OnClosed(e);
    }
}
