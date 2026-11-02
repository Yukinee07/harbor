using System.Runtime.InteropServices;
using System.Windows.Threading;
using Harbor.Dock;
using Harbor.Interop;
using Harbor.Settings;

namespace Harbor.Wallpaper;

internal enum WallpaperTier { Full, Eco, Static, Paused }

/// <summary>
/// UI-thread side of the wallpaper: owns the engine and decides how much it is allowed to do.
/// The policy answers three questions in order:
///   1. Can anyone see it? Display off, locked, remote, fullscreen app => Paused (no work, last frame kept).
///      Monitors hidden behind maximized windows stop individually.
///   2. Is power constrained or is nobody there? Energy Saver, low battery, idle => Static (one frame, then zero work).
///      On battery above the threshold => Eco (lower frame rate and resolution).
///   3. Otherwise Full, where the engine itself runs ambient motion slowly and speeds up only while the
///      cursor is moving over the desktop or music is driving the scene.
/// </summary>
internal sealed class WallpaperController : NativeWindow
{
    static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    const int PBT_POWERSETTINGCHANGE = 0x8013;
    const int WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8, WTS_REMOTE_CONNECT = 0x3, WTS_REMOTE_DISCONNECT = 0x4;

    readonly AppSettings _settings;
    readonly WindowTracker _tracker;
    readonly DispatcherTimer _coverTimer;
    readonly List<IntPtr> _powerNotifications = new();
    WallpaperEngine? _engine;
    bool _onBattery, _energySaver, _locked, _displayOff, _remote, _fullscreen, _idle;
    int _batteryPercent = 100;
    readonly DispatcherTimer _pulse;
    int _pulses;
    AudioAnalyzer? _audio;

    public WallpaperState State { get; private set; } = WallpaperState.Stopped;
    public WallpaperTier Tier { get; private set; } = WallpaperTier.Full;
    /// <summary>Short status, e.g. "Eco" or "Paused".</summary>
    public string StatusText { get; private set; } = "Off";
    /// <summary>Plain-language explanation of the current decision.</summary>
    public string StatusDetail { get; private set; } = "";
    public bool OnBattery => _onBattery;
    public int BatteryPercent => _batteryPercent;
    public event Action? StatusChanged;

    public WallpaperController(AppSettings settings, WindowTracker tracker)
    {
        _settings = settings;
        _tracker = tracker;
        Create("HarborPowerWatch", Native.WS_POPUP, Native.WS_EX_TOOLWINDOW, 0, 0, 0, 0, IntPtr.Zero);

        foreach (var guid in new[] { GUID_CONSOLE_DISPLAY_STATE, GUID_ACDC_POWER_SOURCE })
        {
            var g = guid;
            var h = Native.RegisterPowerSettingNotification(Handle, ref g, 0);
            if (h != IntPtr.Zero) _powerNotifications.Add(h);
        }
        Native.WTSRegisterSessionNotification(Handle, 0);
        _remote = Native.GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0;
        ReadPower();
        try
        {
            _energySaver = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
            Windows.System.Power.PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
        }
        catch (Exception ex) { Log.Warn("Energy saver status unavailable: " + ex.Message); }

        _coverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _coverTimer.Tick += (_, _) => UpdateCoverage();
        _tracker.ForegroundChanged += _ => { _fullscreen = DockController.IsFullscreenForeground(IntPtr.Zero); Evaluate(); UpdateCoverage(); };
        _tracker.LayoutMaybeChanged += () => { if (!_coverTimer.IsEnabled) _coverTimer.Start(); };

        // Idle and battery level change slowly and have no cheap notifications worth the complexity.
        _pulse = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _pulse.Tick += (_, _) => OnPulse();
        _pulse.Start();

        if (_settings.Wallpaper.Enabled) Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public int cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    static double IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref lii) ? unchecked((uint)Environment.TickCount - lii.dwTime) / 1000.0 : 0;
    }

    void OnPulse()
    {
        if (++_pulses % 2 == 0) _engine?.CheckPlacement();
        int minutes = _settings.Wallpaper.IdleMinutes;
        bool idle = minutes > 0 && IdleSeconds() >= minutes * 60;
        int oldPercent = _batteryPercent;
        bool oldBattery = _onBattery;
        ReadPower();
        if (idle != _idle || oldBattery != _onBattery || (_onBattery && oldPercent != _batteryPercent))
        {
            _idle = idle;
            Evaluate();
        }
    }

    void OnEnergySaverChanged(object? sender, object e)
    {
        bool on = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _energySaver = on; Evaluate(); });
    }

    void ReadPower()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return;
        _onBattery = s.ACLineStatus == 0;
        _batteryPercent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 100;
    }

    public void Start()
    {
        if (_engine != null) return;
        _engine = new WallpaperEngine(BuildConfig());
        _engine.StateChanged += s => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnEngineState(s));
        _engine.Start();
        UpdateCoverage();
    }

    public void Stop()
    {
        _engine?.Dispose();
        _engine = null;
        OnEngineState(WallpaperState.Stopped);
    }

    /// <summary>Push changed settings to the engine (or start/stop it).</summary>
    public void Apply()
    {
        if (!_settings.Wallpaper.Enabled) { Stop(); return; }
        if (_engine == null) Start();
        else Evaluate();
    }

    public void Reattach() => _engine?.Reattach();

    public string? LastError => _engine?.LastError;

    /// <summary>The whole policy in one place. Returns the tier and a plain-language reason.</summary>
    public (WallpaperTier tier, string reason) Decide()
    {
        var w = _settings.Wallpaper;
        if (_displayOff) return (WallpaperTier.Paused, "The display is off.");
        if (w.PauseWhenLocked && _locked) return (WallpaperTier.Paused, "Your PC is locked.");
        if (_remote) return (WallpaperTier.Paused, "This is a remote desktop session.");
        if (w.PauseWhenFullscreen && _fullscreen) return (WallpaperTier.Paused, "A fullscreen app or game is in front.");
        if (w.Mode == WallpaperMode.Static) return (WallpaperTier.Static, "You chose Static.");
        if (w.Mode == WallpaperMode.Animated) return (WallpaperTier.Full, "You chose Always animated.");

        if (_energySaver && w.StaticOnEnergySaver) return (WallpaperTier.Static, "Energy Saver is on.");
        if (_onBattery)
        {
            if (_batteryPercent <= w.StaticBelowBatteryPercent)
                return (WallpaperTier.Static, $"Battery is at {_batteryPercent}%, at or below your {w.StaticBelowBatteryPercent}% limit.");
            switch (w.OnBattery)
            {
                case BatteryBehavior.Static: return (WallpaperTier.Static, $"On battery ({_batteryPercent}%).");
                case BatteryBehavior.Eco: return (WallpaperTier.Eco, $"On battery ({_batteryPercent}%): lower frame rate and resolution.");
            }
        }
        if (_idle) return (WallpaperTier.Static, $"No input for {w.IdleMinutes} minutes. It resumes when you're back.");
        return (WallpaperTier.Full, _onBattery ? "On battery, full quality by your choice." : "Plugged in and visible.");
    }

    EngineConfig BuildConfig()
    {
        var w = _settings.Wallpaper;
        var (tier, _) = Decide();
        bool eco = tier == WallpaperTier.Eco;
        int interactive = Math.Clamp(w.FrameRateLimit, 10, 240);
        int ambient = Math.Min(Math.Clamp(w.AmbientFrameRate, 5, 240), interactive);
        return new EngineConfig(
            Scene: w.Scene,
            VideoPath: w.VideoPath,
            Static: tier == WallpaperTier.Static,
            Paused: tier == WallpaperTier.Paused,
            Interactive: w.Interactive,
            MouseStrength: (float)w.MouseStrength,
            Speed: (float)w.Speed,
            InteractiveFps: eco ? Math.Min(interactive, 30) : interactive,
            AmbientFps: eco ? Math.Min(ambient, 20) : ambient,
            RenderScale: eco ? (float)Math.Min(w.RenderScale, 0.6) : (float)w.RenderScale,
            Hue: (float)w.Hue,
            Saturation: (float)w.Saturation,
            Brightness: (float)w.Brightness,
            AudioReactive: w.AudioReactive && SceneCatalog.Get(w.Scene).MusicReactive);
    }

    void Evaluate()
    {
        var config = BuildConfig();
        // Only listen to audio while it can actually affect what's on screen.
        bool wantAudio = config.AudioReactive && !config.Static && !config.Paused && _engine != null;
        if (wantAudio) (_audio ??= new AudioAnalyzer()).Start(); else _audio?.Stop();
        if (_engine != null) _engine.Audio = wantAudio ? _audio : null;
        _engine?.Apply(config);
        UpdateStatus();
    }

    void OnEngineState(WallpaperState s)
    {
        State = s;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var (tier, reason) = Decide();
        string text, detail;
        if (_engine == null) { text = "Off"; detail = "Live wallpaper is turned off."; }
        else if (State == WallpaperState.Failed) { text = "Error"; detail = _engine.LastError ?? "The renderer stopped."; }
        else if (tier == WallpaperTier.Paused) { text = "Paused"; detail = reason; }
        else if (tier == WallpaperTier.Static) { text = "Static"; detail = reason; }
        else if (State == WallpaperState.Paused) { text = "Paused"; detail = "Windows cover the desktop, so there's nothing to draw."; }
        else if (tier == WallpaperTier.Eco) { text = "Eco"; detail = reason; }
        else { text = "Animated"; detail = reason; }
        Tier = tier;
        if (text == StatusText && detail == StatusDetail) return;
        StatusText = text;
        StatusDetail = detail;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Marks monitors whose desktop is hidden: a maximized window, or windows covering ~95% of it
    /// (sampled on a coarse grid). Hidden monitors stop rendering until something moves.
    /// </summary>
    void UpdateCoverage()
    {
        _coverTimer.Stop();
        if (_engine == null || !_settings.Wallpaper.PauseWhenCovered) return;

        var monitors = new List<(IntPtr h, Native.RECT r)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
        {
            monitors.Add((h, Native.GetMonitorInfo(h).rcMonitor));
            return true;
        }, IntPtr.Zero);

        var rects = new List<Native.RECT>();
        var maximizedOn = new HashSet<IntPtr>();
        foreach (var w in _tracker.Windows)
        {
            if (!Native.IsWindowVisible(w.Hwnd) || Native.IsIconic(w.Hwnd) || Native.IsCloaked(w.Hwnd)) continue;
            if (Native.IsZoomed(w.Hwnd)) maximizedOn.Add(Native.MonitorFromWindow(w.Hwnd, Native.MONITOR_DEFAULTTONEAREST));
            rects.Add(Native.GetVisibleBounds(w.Hwnd));
        }

        foreach (var (h, r) in monitors)
        {
            bool covered = maximizedOn.Contains(h) || CoverageFraction(r, rects) >= 0.95;
            _engine.SetCovered(h, covered);
        }
    }

    static double CoverageFraction(Native.RECT monitor, List<Native.RECT> windows)
    {
        const int cols = 24, rows = 14;
        int hit = 0;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            int px = monitor.Left + (int)((x + 0.5) * monitor.Width / cols);
            int py = monitor.Top + (int)((y + 0.5) * monitor.Height / rows);
            foreach (var w in windows)
                if (w.Contains(px, py)) { hit++; break; }
        }
        return hit / (double)(cols * rows);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POWERBROADCAST_SETTING { public Guid PowerSetting; public int DataLength; public int Data; }

    protected override bool WndProc(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case Native.WM_POWERBROADCAST:
                if ((int)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
                {
                    var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                    if (s.PowerSetting == GUID_CONSOLE_DISPLAY_STATE) _displayOff = s.Data == 0;
                    else if (s.PowerSetting == GUID_ACDC_POWER_SOURCE) { _onBattery = s.Data != 0; ReadPower(); } // 0 = AC
                    Evaluate();
                }
                else if ((int)wParam == 0x7 /* PBT_APMRESUMESUSPEND */ || (int)wParam == 0x12 /* PBT_APMRESUMEAUTOMATIC */)
                {
                    ReadPower();
                    Evaluate();
                }
                result = (IntPtr)1;
                return true;

            case Native.WM_WTSSESSION_CHANGE:
                switch ((int)wParam)
                {
                    case WTS_SESSION_LOCK: _locked = true; break;
                    case WTS_SESSION_UNLOCK: _locked = false; break;
                    case WTS_REMOTE_CONNECT: _remote = true; break;
                    case WTS_REMOTE_DISCONNECT: _remote = Native.GetSystemMetrics(0x1000) != 0; break;
                }
                Evaluate();
                return true;

            case Native.WM_DISPLAYCHANGE:
                _engine?.Reattach();
                return false;
        }
        return false;
    }

    public override void Dispose()
    {
        _pulse.Stop();
        _audio?.Dispose();
        try { Windows.System.Power.PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged; } catch { }
        foreach (var h in _powerNotifications) Native.UnregisterPowerSettingNotification(h);
        if (Handle != IntPtr.Zero) Native.WTSUnRegisterSessionNotification(Handle);
        _engine?.Dispose();
        _engine = null;
        base.Dispose();
    }
}
