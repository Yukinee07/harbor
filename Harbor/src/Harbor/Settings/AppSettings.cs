using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Settings;

public enum ThemeMode { System, Dark, Light }
public enum AutoHideMode { Never, WhenOverlapped, Always }
public enum WallpaperMode { Smart, Animated, Static }
public enum BatteryBehavior { Eco, Static, Full }

public sealed class PinnedApp
{
    /// <summary>Executable path, or "shell:AppsFolder\{AUMID}" for packaged / shell apps.</summary>
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Arguments { get; set; }
    /// <summary>AppUserModelID if known; used to match running windows of packaged apps.</summary>
    public string? AppId { get; set; }
}

public static class DockLimits
{
    public const double MinIcon = 28, MaxIcon = 96, MaxZoom = 1.2;
}

public sealed class DockSettings
{
    public double IconSize { get; set; } = 50;
    public bool Magnification { get; set; } = true;
    public double MagnificationAmount { get; set; } = 0.55;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public double TintOpacity { get; set; } = 0.55;
    public double CornerRoundness { get; set; } = 0.34;
    public AutoHideMode AutoHide { get; set; } = AutoHideMode.Never;
    public bool HideWindowsTaskbar { get; set; } = false;
    public bool ShowStartButton { get; set; } = true;
    public bool ShowRecycleBin { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool BounceOnLaunch { get; set; } = true;
    public bool ShowRunningIndicators { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;
    public bool ShowNowPlaying { get; set; } = true;
    public bool ShowHarborButton { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowPomodoro { get; set; } = true;
    public bool ShowOnAllScreens { get; set; } = true;
    public bool WindowPreviews { get; set; } = true;
}

public sealed class WallpaperSettings
{
    public bool Enabled { get; set; } = true;
    public string Scene { get; set; } = "aurora";
    public string? VideoPath { get; set; }
    /// <summary>Smart adapts to power, visibility and attention; Animated always moves when visible; Static never moves.</summary>
    public WallpaperMode Mode { get; set; } = WallpaperMode.Smart;
    public bool Interactive { get; set; } = true;
    public double MouseStrength { get; set; } = 1.0;
    public double Speed { get; set; } = 1.0;
    /// <summary>Frame rate while you interact with the desktop (cursor moving over it).</summary>
    public int FrameRateLimit { get; set; } = 60;
    /// <summary>Frame rate for ambient motion when nobody is interacting. Slow scenes look the same at half the cost.</summary>
    public int AmbientFrameRate { get; set; } = 30;
    public BatteryBehavior OnBattery { get; set; } = BatteryBehavior.Eco;
    /// <summary>Below this charge the wallpaper goes static even in Eco.</summary>
    public int StaticBelowBatteryPercent { get; set; } = 35;
    /// <summary>Freeze after this long without keyboard or mouse input (0 = never).</summary>
    public int IdleMinutes { get; set; } = 5;
    /// <summary>Internal render resolution relative to the monitor (1.0 = native, e.g. full 4K).</summary>
    public double RenderScale { get; set; } = 0.75;
    public bool StaticOnBattery { get; set; } = true;
    public bool StaticOnEnergySaver { get; set; } = true;
    public bool PauseWhenCovered { get; set; } = true;
    public bool PauseWhenFullscreen { get; set; } = true;
    public bool PauseWhenLocked { get; set; } = true;
    public double Hue { get; set; } = 0;
    public double Saturation { get; set; } = 1.0;
    public double Brightness { get; set; } = 1.0;
    public Dictionary<string, Dictionary<string, double>> SceneValues { get; set; } = new();
}

public sealed class AppSettings
{
    public int Version { get; set; } = 3;
    public DockSettings Dock { get; set; } = new();
    public WallpaperSettings Wallpaper { get; set; } = new();
    public PomodoroSettings Pomodoro { get; set; } = new();
    public List<PinnedApp> Pinned { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool FirstRunDone { get; set; }

    [JsonIgnore] public static string Directory { get; } = Log.TestProfile != null
        ? System.IO.Path.Combine(Log.TestProfile, "roaming")
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Harbor");
    [JsonIgnore] static string FilePath => System.IO.Path.Combine(Directory, "settings.json");

    static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), s_json);
                if (s != null)
                {
                    Migrate(s);
                    return s;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {path}: {ex.Message}");
            }
        }
        return new AppSettings();
    }

    /// <summary>
    /// Version 1 had only Animated and Static, and "Animated" still went static on battery. That behaviour
    /// is now called Smart; "Animated" means always animated, so old files are moved across.
    /// </summary>
    static void Migrate(AppSettings s)
    {
        if (s.Version < 2)
        {
            if (s.Wallpaper.Mode == WallpaperMode.Animated) s.Wallpaper.Mode = WallpaperMode.Smart;
        }
        // "When covered" used to be the default, which made the dock vanish whenever a window overlapped
        // it. The dock now stays on top unless auto-hide is chosen on purpose.
        if (s.Version < 3 && s.Dock.AutoHide == AutoHideMode.WhenOverlapped) s.Dock.AutoHide = AutoHideMode.Never;
        s.Version = 3;
    }

    /// <summary>Atomic save: write a temp file, keep the previous file as .bak, then swap.</summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, s_json));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
