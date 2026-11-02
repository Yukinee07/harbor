using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Harbor.Dock;
using Harbor.Interop;
using Harbor.Settings;

namespace Harbor.UI;

/// <summary>Swaps the WPF brush palette between dark and light, following the dock's theme.</summary>
internal static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    public static void Apply(DockSettings settings)
    {
        var theme = DockTheme.Resolve(settings);
        IsDark = theme.Dark;
        var accent = Color.FromArgb(255, theme.Accent.R, theme.Accent.G, theme.Accent.B);
        var r = Application.Current.Resources;
        void Set(string key, Color c) => r[key] = Freeze(new SolidColorBrush(c));

        if (IsDark)
        {
            Set("H.Window", Color.FromRgb(0x1B, 0x1B, 0x1F));
            Set("H.Sidebar", Color.FromRgb(0x15, 0x15, 0x18));
            Set("H.Card", Color.FromRgb(0x25, 0x25, 0x2A));
            Set("H.CardHover", Color.FromRgb(0x2D, 0x2D, 0x33));
            Set("H.Border", Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            Set("H.Text", Color.FromRgb(0xF3, 0xF3, 0xF5));
            Set("H.TextDim", Color.FromRgb(0x9C, 0x9C, 0xA6));
            Set("H.Hover", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set("H.Pressed", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            Set("H.Track", Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            Set("H.MenuBg", Color.FromArgb(0xF4, 0x2A, 0x2A, 0x2F));
            Set("H.Layer", Color.FromArgb(0x4C, 0x3A, 0x3A, 0x40));
            Set("H.LayerBorder", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set("H.Group", Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
            Set("H.GroupBorder", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set("H.Window", Color.FromRgb(0xF3, 0xF3, 0xF6));
            Set("H.Sidebar", Color.FromRgb(0xEA, 0xEA, 0xEF));
            Set("H.Card", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("H.CardHover", Color.FromRgb(0xF7, 0xF7, 0xFA));
            Set("H.Border", Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
            Set("H.Text", Color.FromRgb(0x1A, 0x1A, 0x1E));
            Set("H.TextDim", Color.FromRgb(0x47, 0x55, 0x69)); // slate-600: keeps 4.5:1 on light surfaces
            Set("H.Hover", Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
            Set("H.Pressed", Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
            Set("H.Track", Color.FromArgb(0x2A, 0x00, 0x00, 0x00));
            Set("H.MenuBg", Color.FromArgb(0xF7, 0xFB, 0xFB, 0xFD));
            Set("H.Layer", Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            Set("H.LayerBorder", Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            Set("H.Group", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
            Set("H.GroupBorder", Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        }
        Set("H.Accent", accent);
        // Pick readable text on the accent colour.
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        Set("H.AccentText", lum > 0.62 ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White);
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Mica needs Windows 11 22H2 or later; older systems get a solid window background.</summary>
    public static bool MicaSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Dark title bar, rounded corners and (when supported) the Mica backdrop for a WPF window.</summary>
    public static void StyleWindow(Window w, bool mica = false)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Native.SetDwmInt(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, IsDark ? 1 : 0);
        Native.SetDwmInt(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2 /* round */);
        if (mica && MicaSupported)
        {
            if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.SetDwmInt(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 2 /* Mica */);
        }
    }

    /// <summary>Windows "Animation effects" setting; when off, Harbor's UI skips decorative motion.</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            bool on = true;
            try { on = SystemParameters.ClientAreaAnimation; } catch { }
            return on;
        }
    }
}

/// <summary>Small helpers for building the dock and tray context menus.</summary>
internal static class Menus
{
    public static MenuItem Item(string header, Action onClick, string? glyph = null, bool isChecked = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Tag = glyph, IsChecked = isChecked, IsEnabled = enabled };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    public static MenuItem Label(string text) => new() { Header = text, IsEnabled = false, Tag = "" };

    /// <summary>
    /// Opens a menu at the cursor. Harbor's windows never take focus, so the menu's own popup is made
    /// foreground; otherwise clicking elsewhere wouldn't dismiss it.
    /// </summary>
    public static void Show(ContextMenu menu, Action? closed = null, Native.RECT? anchor = null, bool takeFocus = true)
    {
        // WPF never sees mouse input on Harbor's raw windows, so MousePoint would use a stale position.
        // Place explicitly, converting physical pixels to WPF's device-independent units.
        Native.GetCursorPos(out var pt);
        // The screen the menu is going to, which WPF also uses to choose the scaling it draws at, so it has to be right from the start.
        var aimedAt = anchor is { } aim ? new Native.POINT { X = (aim.Left + aim.Right) / 2, Y = aim.Top } : pt;
        double scale = Native.GetMonitorScale(Native.MonitorFromPoint(aimedAt, Native.MONITOR_DEFAULTTONEAREST));
        menu.Placement = PlacementMode.AbsolutePoint;
        if (anchor is { } a)
        {
            // Centered above the icon, like the macOS dock.
            menu.ApplyTemplate();
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = menu.DesiredSize;
            menu.HorizontalOffset = (a.Left + a.Right) / 2.0 / scale - size.Width / 2;
            menu.VerticalOffset = a.Top / scale - size.Height + 8;
        }
        else
        {
            menu.HorizontalOffset = pt.X / scale;
            menu.VerticalOffset = pt.Y / scale;
        }
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is not HwndSource src) return;
            // A popup keeps drawing at the scale of the screen it was created on, and moving it doesn't change that.
            // So if that was another screen, scale the contents to make up the difference.
            double drawnAt = src.CompositionTarget.TransformToDevice.M11;
            if (Math.Abs(drawnAt - scale) > 0.01) menu.LayoutTransform = new ScaleTransform(scale / drawnAt, scale / drawnAt);
            PlaceInPixels(src.Handle, pt, anchor);
            // WPF can size and place a popup again as it settles on a screen with different scaling.
            menu.Dispatcher.BeginInvoke(() => { if (menu.IsOpen) PlaceInPixels(src.Handle, pt, anchor); }, System.Windows.Threading.DispatcherPriority.Loaded);
            if (!takeFocus) return;
            WindowActions.TapKey(0xE8);
            Native.SetForegroundWindow(src.Handle);
        };
        menu.Closed += (_, _) => closed?.Invoke();
        menu.IsOpen = true;
    }

    /// <summary>
    /// The offsets above are in WPF's units, which it converts using the scaling of the screen the popup was created on, so
    /// they land in the wrong place on a screen scaled differently. This puts it in the right place in real pixels.
    /// </summary>
    static void PlaceInPixels(IntPtr popup, Native.POINT cursor, Native.RECT? anchor)
    {
        var target = anchor is { } a ? new Native.POINT { X = (a.Left + a.Right) / 2, Y = a.Top } : cursor;
        var monitor = Native.MonitorFromPoint(target, Native.MONITOR_DEFAULTTONEAREST);
        const uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        if (Native.MonitorFromWindow(popup, Native.MONITOR_DEFAULTTONEAREST) != monitor)
            Native.SetWindowPos(popup, IntPtr.Zero, target.X, target.Y, 0, 0, flags);
        Native.GetWindowRect(popup, out var r);
        double scale = Native.GetMonitorScale(monitor);
        int x = anchor != null ? target.X - r.Width / 2 : cursor.X;
        int y = anchor != null ? target.Y - r.Height + (int)(8 * scale) : cursor.Y;
        var mi = Native.GetMonitorInfo(monitor);
        x = Math.Clamp(x, mi.rcMonitor.Left, Math.Max(mi.rcMonitor.Left, mi.rcMonitor.Right - r.Width));
        y = Math.Clamp(y, mi.rcMonitor.Top, Math.Max(mi.rcMonitor.Top, mi.rcMonitor.Bottom - r.Height));
        Native.SetWindowPos(popup, IntPtr.Zero, x, y, 0, 0, flags);
    }
}
