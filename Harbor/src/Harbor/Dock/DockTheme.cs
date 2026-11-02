using Harbor.Settings;
using Microsoft.Win32;
using Windows.UI;

namespace Harbor.Dock;

/// <summary>Resolved colors for the dock surfaces. Recomputed when the system theme or settings change.</summary>
internal sealed record DockTheme(
    bool Dark,
    Color TintTop,
    Color TintBottom,
    Color StrokeTop,
    Color StrokeBottom,
    Color Outline,
    Color Dot,
    Color Accent,
    Color LabelBackground,
    Color LabelText,
    Color LabelBorder,
    Color Separator,
    float ShadowOpacity,
    bool TransparencyEnabled)
{
    // Quick panel: stronger tint than the bar because text sits on it, and text kept at 4.5:1 or better.
    public Color PanelTop => Dark ? Color.FromArgb(PanelAlpha(205), 44, 44, 50) : Color.FromArgb(PanelAlpha(215), 255, 255, 255);
    public Color PanelBottom => Dark ? Color.FromArgb(PanelAlpha(228), 26, 26, 30) : Color.FromArgb(PanelAlpha(228), 240, 240, 244);
    public Color PanelDim => Dark ? Color.FromArgb(255, 176, 180, 190) : Color.FromArgb(255, 71, 85, 105);
    public Color PanelFaint => Dark ? Color.FromArgb(120, 176, 180, 190) : Color.FromArgb(130, 71, 85, 105);
    public Color PanelTrack => Dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(44, 0, 0, 0);
    public Color PanelHover => Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
    public Color PanelDivider => Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
    public Color AccentText => (0.299 * Accent.R + 0.587 * Accent.G + 0.114 * Accent.B) / 255 > 0.62
        ? Color.FromArgb(255, 20, 20, 24) : Color.FromArgb(255, 255, 255, 255);
    byte PanelAlpha(byte a) => TransparencyEnabled ? a : (byte)245;
    public Color BatteryNormal => Dark ? Color.FromArgb(235, 235, 235, 240) : Color.FromArgb(235, 40, 40, 46);
    public Color BatteryCharging => Dark ? Color.FromArgb(255, 52, 199, 89) : Color.FromArgb(255, 22, 163, 74);
    public Color BatteryLow => Dark ? Color.FromArgb(255, 255, 95, 87) : Color.FromArgb(255, 220, 38, 38);
    public Color BatterySaver => Dark ? Color.FromArgb(255, 255, 179, 64) : Color.FromArgb(255, 217, 119, 6);

    public static DockTheme Resolve(DockSettings s)
    {
        bool dark = s.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemUsesLightTheme(),
        };
        bool transparency = TransparencyEffectsEnabled();
        // Without host backdrop the tint is the whole look, so keep it nearly opaque.
        double o = transparency ? Math.Clamp(s.TintOpacity, 0.05, 1.0) : 0.94;
        byte A(double a) => (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
        var accent = AccentColor();

        return dark
            ? new DockTheme(true,
                TintTop: Color.FromArgb(A(o * 0.85), 44, 44, 50),
                TintBottom: Color.FromArgb(A(o), 24, 24, 28),
                StrokeTop: Color.FromArgb(70, 255, 255, 255),
                StrokeBottom: Color.FromArgb(18, 255, 255, 255),
                Outline: Color.FromArgb(90, 0, 0, 0),
                Dot: Color.FromArgb(210, 235, 235, 240),
                Accent: Lighten(accent, 0.25),
                LabelBackground: Color.FromArgb(235, 36, 36, 40),
                LabelText: Color.FromArgb(255, 245, 245, 247),
                LabelBorder: Color.FromArgb(40, 255, 255, 255),
                Separator: Color.FromArgb(60, 255, 255, 255),
                ShadowOpacity: 0.45f,
                TransparencyEnabled: transparency)
            : new DockTheme(false,
                TintTop: Color.FromArgb(A(o * 0.9), 255, 255, 255),
                TintBottom: Color.FromArgb(A(o * 0.95), 236, 236, 240),
                StrokeTop: Color.FromArgb(200, 255, 255, 255),
                StrokeBottom: Color.FromArgb(40, 255, 255, 255),
                Outline: Color.FromArgb(40, 0, 0, 0),
                Dot: Color.FromArgb(200, 40, 40, 46),
                Accent: accent,
                LabelBackground: Color.FromArgb(240, 250, 250, 252),
                LabelText: Color.FromArgb(255, 24, 24, 28),
                LabelBorder: Color.FromArgb(30, 0, 0, 0),
                Separator: Color.FromArgb(55, 0, 0, 0),
                ShadowOpacity: 0.28f,
                TransparencyEnabled: transparency);
    }

    static bool SystemUsesLightTheme() => ReadPersonalize("SystemUsesLightTheme", 0) == 1;
    static bool TransparencyEffectsEnabled() => ReadPersonalize("EnableTransparency", 1) == 1;

    static int ReadPersonalize(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    static Color AccentColor()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            return ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Color.FromArgb(255, 0, 120, 212);
        }
    }

    static Color Lighten(Color c, double t) => Color.FromArgb(c.A,
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
}
