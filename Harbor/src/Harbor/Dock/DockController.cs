using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Harbor.Interop;
using Harbor.Settings;

namespace Harbor.Dock;

/// <summary>
/// Turns pinned apps and tracked windows into dock items and implements what clicking them does.
/// </summary>
internal sealed class DockController : IDisposable
{
    const string RecycleBinId = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    readonly AppSettings _settings;
    readonly Dispatcher _ui;
    readonly Dictionary<string, DockItem> _items = new();
    readonly DispatcherTimer _overlapTimer, _clockTimer;
    readonly List<DockWindow> _docks = new();
    string? _screensSeen;
    Func<WallpaperMode>? _currentWallpaperMode;
    bool _pressedWasActive;
    bool _startMenuHidden;

    /// <summary>One per screen, or just one on the main screen if that's what's chosen. The main screen's is first.</summary>
    public IReadOnlyList<DockWindow> Docks => _docks;

    /// <summary>Asked when a panel opens, because the docks don't own wallpaper settings.</summary>
    public Func<WallpaperMode>? CurrentWallpaperMode
    {
        get => _currentWallpaperMode;
        set
        {
            _currentWallpaperMode = value;
            foreach (var dock in _docks) dock.CurrentWallpaperMode = value;
        }
    }
    public MediaController Media { get; }
    public WindowTracker Tracker { get; }
    public AppCatalog Catalog { get; }
    public IReadOnlyList<DockItem> Items { get; private set; } = Array.Empty<DockItem>();

    public event Action<DockWindow, DockItem?, Native.POINT>? ContextMenuRequested;
    public event Action<DockWindow, DockItem?>? HoverItemChanged;
    public event Action<DockWindow, DockItem>? ItemPressed;
    public event Action<WallpaperMode>? WallpaperModeChosen;
    /// <summary>A dock is about to go away because its screen did.</summary>
    public event Action? DocksChanged;
    public event Action? SettingsChanged;
    public event Action? SettingsApplied;
    public event Action? PomodoroClicked;
    public event Action? OpenAppRequested;

    public DockController(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        Catalog = new AppCatalog(ui);
        Diag.Mark("  catalog started");
        Tracker = new WindowTracker();
        Diag.Mark("  window tracker");
        Media = new MediaController(ui);
        Diag.Mark("  media");
        Media.Changed += Rebuild;

        Catalog.IconLoaded += OnIconLoaded;
        Catalog.CatalogLoaded += () => { RefreshNames(); Rebuild(); };
        Tracker.Changed += Rebuild;
        Tracker.Flashed += OnFlashed;
        Tracker.ForegroundChanged += _ =>
        {
            CheckFullscreen();
            CheckStartMenu();
            ScheduleOverlapCheck();
            foreach (var dock in _docks) dock.KeepOnTop();
        };
        Tracker.LayoutMaybeChanged += ScheduleOverlapCheck;
        Tracker.ExplorerRestarted += () =>
        {
            TaskbarController.Reapply();
            foreach (var dock in _docks) dock.Reposition();
        };

        _overlapTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(120) };
        _overlapTimer.Tick += (_, _) =>
        {
            _overlapTimer.Stop();
            SyncDocks();
            CheckOverlap();
        };

        // Wakes once a minute, on the minute, rather than ticking every second to notice.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Normal, ui);
        _clockTimer.Tick += (_, _) => TickClock();
        ScheduleClockTick();

        SyncDocks();
        Diag.Mark("  docks");
        if (_settings.Dock.HideWindowsTaskbar) SetTaskbarHidden(true);
        Rebuild();
        ScheduleOverlapCheck();
    }

    // ------------------------------------------------------------------ one dock per screen

    /// <summary>
    /// Brings the docks in line with the screens. Called often and cheap when nothing changed: it looks at what's actually
    /// connected, rather than trusting any one notification, because Windows sends several as a screen comes and goes.
    /// </summary>
    void SyncDocks()
    {
        var monitors = Native.AllMonitors();
        if (!_settings.Dock.ShowOnAllScreens) monitors = monitors.Take(1).ToList();
        var screens = monitors.Select(m => (Handle: m, Device: Native.GetMonitorDeviceName(m))).ToList();
        string seen = string.Join("|", screens.Select(s =>
        {
            var mi = Native.GetMonitorInfo(s.Handle);
            return $"{s.Device} {mi.rcMonitor} {mi.rcWork} {s.Handle}";
        }));
        if (seen == _screensSeen) return;
        bool firstTime = _screensSeen == null;
        _screensSeen = seen;

        var gone = _docks.Where(d => screens.All(s => s.Device != d.DeviceName)).ToList();
        if (gone.Count > 0) DocksChanged?.Invoke();
        foreach (var dock in gone)
        {
            _docks.Remove(dock);
            dock.Dispose();
        }

        var ordered = new List<DockWindow>();
        foreach (var (handle, device) in screens)
        {
            var dock = _docks.FirstOrDefault(d => d.DeviceName == device);
            if (dock != null) dock.MoveToMonitor(handle);
            else dock = AddDock(handle, device);
            ordered.Add(dock);
        }
        _docks.Clear();
        _docks.AddRange(ordered);

        ExternalScreens.Shared.Probe();
        if (!firstTime)
        {
            Log.Info($"Docks on: {string.Join(", ", screens.Select(s => s.Device))}");
            CheckFullscreen();
            CheckOverlap();
        }
    }

    DockWindow AddDock(IntPtr monitor, string device)
    {
        var dock = new DockWindow(_settings.Dock, monitor, device) { CurrentWallpaperMode = _currentWallpaperMode };
        dock.ItemPressed += item =>
        {
            _pressedWasActive = item.IsActive;
            ItemPressed?.Invoke(dock, item);
        };
        dock.ItemInvoked += OnInvoked;
        dock.ContextMenuRequested += (item, pt) => ContextMenuRequested?.Invoke(dock, item, pt);
        dock.HoverItemChanged += item => HoverItemChanged?.Invoke(dock, item);
        dock.IconSizeWheel += OnIconSizeWheel;
        dock.ItemReordered += OnReordered;
        dock.ItemScrolled += OnScrolled;
        dock.OpenAppRequested += () => OpenAppRequested?.Invoke();
        dock.WallpaperModeChosen += mode => WallpaperModeChosen?.Invoke(mode);
        dock.ItemDraggedOff += item => _ui.BeginInvoke(() => Unpin(item), DispatcherPriority.Background);
        dock.HoverChanged += hovering => { if (!hovering) ScheduleOverlapCheck(); };
        dock.DisplayChanged += ScheduleOverlapCheck;
        if (Items.Count > 0) dock.SetItems(Items);
        return dock;
    }

    // ------------------------------------------------------------------ clock

    void ScheduleClockTick()
    {
        var now = DateTime.Now;
        _clockTimer.Stop();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(60_000 - (now.Second * 1000 + now.Millisecond) + 20);
        _clockTimer.Start();
    }

    void TickClock()
    {
        // Picks up a changed time zone or region without needing a restart.
        System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
        TimeZoneInfo.ClearCachedData();
        if (Items.FirstOrDefault(i => i.Kind == DockItemKind.Clock) is { } clock)
        {
            clock.IconVersion++;
            foreach (var dock in _docks) dock.Refresh(clock);
        }
        ScheduleClockTick();
    }

    // ------------------------------------------------------------------ item model

    DockItem Get(string key, DockItemKind kind)
    {
        if (!_items.TryGetValue(key, out var item))
        {
            item = new DockItem(key, kind);
            _items[key] = item;
        }
        return item;
    }

    public void Rebuild()
    {
        var groups = Tracker.Windows.GroupBy(w => w.GroupKey).ToList();
        var claimed = new HashSet<string>();
        var list = new List<DockItem>();

        if (_settings.Dock.ShowStartButton)
        {
            var start = Get("harbor:start", DockItemKind.Start);
            start.Name = "Start";
            list.Add(start);
        }

        // An open app belongs on its pin, however many groups its windows fall into.
        var pinned = new List<(DockItem Item, List<TrackedWindow> Windows)>();
        foreach (var pin in _settings.Pinned)
        {
            var item = Get("pin:" + pin.Path.ToLowerInvariant(), DockItemKind.App);
            item.Pin = pin;
            var mine = groups.Where(g => !claimed.Contains(g.Key) && g.Any(w => Matches(pin, w))).ToList();
            foreach (var g in mine) claimed.Add(g.Key);
            var windows = mine.SelectMany(g => g).ToList();
            var first = windows.FirstOrDefault();
            item.ExePath = pin.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? first?.ExePath : pin.Path;
            item.AppId = pin.AppId ?? first?.AppId ?? (pin.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ? pin.Path[17..] : null);
            item.Name = !string.IsNullOrWhiteSpace(pin.Name) ? pin.Name : Catalog.NameFor(item.AppId, item.ExePath);
            pinned.Add((item, windows));
            list.Add(item);
        }

        // Two icons with one name read as a duplicate whatever their ids say, so the name settles the rest.
        // Sites installed as apps stay apart, because they all run from the browser's exe.
        static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var running = new List<(string Key, string Name, List<TrackedWindow> Windows)>();
        foreach (var group in groups.Where(g => !claimed.Contains(g.Key)).OrderBy(g => g.Min(w => w.FirstSeenTick)))
        {
            var first = group.First();
            string name = Catalog.NameFor(first.AppId, first.ExePath, first.Title);
            var windows = AppIdentity.IsWebApp(first.AppId) ? null
                : pinned.FirstOrDefault(p => SameName(p.Item.Name, name)).Windows ?? running.FirstOrDefault(r => SameName(r.Name, name)).Windows;
            if (windows != null) windows.AddRange(group);
            else running.Add((group.Key, name, group.ToList()));
        }

        foreach (var (item, windows) in pinned) FillWindows(item, windows);
        if (running.Count > 0 && _settings.Pinned.Count > 0) list.Add(Get("harbor:sep-running", DockItemKind.Separator));
        foreach (var (key, name, windows) in running)
        {
            var first = windows[0];
            var item = Get("run:" + key, DockItemKind.App);
            item.Pin = null;
            item.ExePath = first.ExePath;
            item.AppId = first.AppId;
            item.Name = name;
            FillWindows(item, windows);
            list.Add(item);
        }

        bool media = _settings.Dock.ShowNowPlaying && Media.HasSession;
        if (_settings.Dock.ShowRecycleBin || media || _settings.Dock.ShowHarborButton || _settings.Dock.ShowClock || _settings.Dock.ShowPomodoro) list.Add(Get("harbor:sep-bin", DockItemKind.Separator));
        if (media)
        {
            var m = Get("harbor:media", DockItemKind.Media);
            m.Name = Media.Label;
            m.IsActive = Media.IsPlaying;
            if (!ReferenceEquals(m.Icon, Media.Art) || m.IconVersion != Media.Version)
            {
                m.Icon = Media.Art;
                m.IconVersion = Media.Version;
            }
            list.Add(m);
        }
        if (_settings.Dock.ShowRecycleBin)
        {
            var bin = Get("harbor:bin", DockItemKind.RecycleBin);
            bin.Name = "Recycle Bin";
            list.Add(bin);
        }

        if (_settings.Dock.ShowPomodoro)
        {
            var timer = Get("harbor:pomodoro", DockItemKind.Pomodoro);
            timer.Name = "Pomodoro";
            ApplyPomodoro(timer);
            list.Add(timer);
        }

        if (_settings.Dock.ShowHarborButton)
        {
            var harbor = Get("harbor:app", DockItemKind.Harbor);
            harbor.Name = "Harbor";
            list.Add(harbor);
        }

        if (_settings.Dock.ShowClock)
        {
            var clock = Get("harbor:clock", DockItemKind.Clock);
            clock.Name = "Clock";
            list.Add(clock);
        }

        foreach (var item in list) if (item.Kind != DockItemKind.Media) EnsureIcon(item);

        // Drop state for items that disappeared so a re-pin starts fresh.
        var live = list.Select(i => i.Key).ToHashSet();
        foreach (var stale in _items.Keys.Where(k => !live.Contains(k)).ToList()) _items.Remove(stale);

        Items = list;
        foreach (var dock in _docks) dock.SetItems(list);
        Diag.Items(list);
    }

    void FillWindows(DockItem item, List<TrackedWindow> windows)
    {
        item.Windows.Clear();
        item.IsActive = false;
        item.NeedsAttention = false;
        if (windows.Count == 0) return;
        var fg = Tracker.Foreground;
        var fgRoot = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
        foreach (var w in windows.OrderByDescending(w => w.LastActiveTick).ThenBy(w => w.FirstSeenTick))
        {
            item.Windows.Add(w.Hwnd);
            if (w.Hwnd == fg || w.Hwnd == fgRoot) item.IsActive = true;
            if (w.Flashing) item.NeedsAttention = true;
        }
    }

    static bool Matches(PinnedApp pin, TrackedWindow w)
    {
        bool shell = pin.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase);
        string? shellId = shell ? pin.Path[17..] : null;
        string? pinExe = shell ? AppCatalog.AppIdToPath(shellId!) : pin.Path;
        return AppIdentity.Matches(pin.AppId ?? shellId, pinExe, w.AppId, w.ExePath, w.Family);
    }

    string? IconSource(DockItem item)
    {
        switch (item.Kind)
        {
            case DockItemKind.RecycleBin: return RecycleBinId;
            case DockItemKind.App:
                if (item.Pin != null && item.Pin.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return item.Pin.Path;
                if (item.AppId != null && (item.AppId.Contains('!') || item.AppId == "Microsoft.Windows.Explorer"))
                    return @"shell:AppsFolder\" + item.AppId;
                return item.ExePath ?? item.Pin?.Path;
        }
        return null;
    }

    void EnsureIcon(DockItem item)
    {
        var source = IconSource(item);
        if (source == null) return;
        var icon = Catalog.GetIcon(source, item.ExePath);
        if (icon != null && !ReferenceEquals(icon, item.Icon))
        {
            item.Icon = icon;
            item.IconVersion++;
        }
    }

    void OnIconLoaded(string source)
    {
        foreach (var item in Items)
        {
            if (!string.Equals(IconSource(item), source, StringComparison.OrdinalIgnoreCase)) continue;
            var icon = Catalog.GetIcon(source, item.ExePath);
            if (icon == null && item.Windows.Count > 0) icon = WindowIcon(item.Windows[0]);
            if (icon == null || ReferenceEquals(icon, item.Icon)) continue;
            item.Icon = icon;
            item.IconVersion++;
            foreach (var dock in _docks) dock.Refresh(item);
        }
    }

    static IconImage? WindowIcon(IntPtr hwnd)
    {
        foreach (var kind in new[] { Native.ICON_BIG, Native.ICON_SMALL2 })
        {
            if (Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)kind, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out var h) != IntPtr.Zero && h != IntPtr.Zero)
                return ShellIcons.FromHIcon(h);
        }
        var cls = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        return cls != IntPtr.Zero ? ShellIcons.FromHIcon(cls) : null;
    }

    void RefreshNames()
    {
        foreach (var item in Items.Where(i => i.Kind == DockItemKind.App))
            if (item.Pin == null || string.IsNullOrWhiteSpace(item.Pin.Name)) item.Name = Catalog.NameFor(item.AppId, item.ExePath, item.Name);
    }

    // ------------------------------------------------------------------ actions

    void OnInvoked(DockItem item, DockButton button)
    {
        switch (item.Kind)
        {
            case DockItemKind.Start:
                WindowActions.OpenStartMenu();
                return;
            case DockItemKind.RecycleBin:
                ShellOpen("shell:RecycleBinFolder");
                return;
            case DockItemKind.Media:
                Media.PlayPause();
                return;
            case DockItemKind.Pomodoro:
                PomodoroClicked?.Invoke();
                return;
            case DockItemKind.Harbor:
                OpenAppRequested?.Invoke();
                return;
            case DockItemKind.App:
                break;
            default:
                return;
        }

        if (button == DockButton.Middle || !item.IsRunning)
        {
            Launch(item);
            return;
        }

        var windows = item.Windows.Where(Native.IsWindow).ToList();
        if (windows.Count == 0) { Launch(item); return; }

        if (_pressedWasActive)
        {
            if (windows.Count == 1)
            {
                WindowActions.Minimize(windows[0]);
                return;
            }
            // Several windows: cycle through them in a stable order, like Alt+` on macOS.
            var stable = Tracker.Windows.Where(w => windows.Contains(w.Hwnd)).OrderBy(w => w.FirstSeenTick).Select(w => w.Hwnd).ToList();
            var fgRoot = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOTOWNER);
            int i = stable.IndexOf(fgRoot);
            WindowActions.Activate(stable[(i + 1) % stable.Count]);
            return;
        }
        WindowActions.Activate(windows[0]);
    }

    public void Launch(DockItem item, bool asAdmin = false)
    {
        try
        {
            string? path = item.Pin?.Path ?? item.ExePath;
            string? args = item.Pin?.Arguments;
            if (path != null && path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                if (!ShellInterop.ActivatePackagedApp(path[17..], args)) ShellOpen(path);
            }
            else if (path == null && item.AppId != null && ShellInterop.ActivatePackagedApp(item.AppId)) { }
            else if (path != null)
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = true, Arguments = args ?? "" };
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                if (asAdmin) psi.Verb = "runas";
                Process.Start(psi);
            }
            else return;

            item.LaunchingUntil = DateTime.UtcNow.AddSeconds(10);
            foreach (var dock in _docks) dock.Bounce(item, item.IsRunning ? 1 : 3);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled UAC.
        }
        catch (Exception ex)
        {
            Log.Error($"Launch failed for {item}", ex);
        }
    }

    static void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Open {target} failed", ex); }
    }

    public void OpenFileLocation(DockItem item)
    {
        var path = item.ExePath ?? item.Pin?.Path;
        if (path == null || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public void CloseAll(DockItem item)
    {
        foreach (var w in item.Windows.ToList()) WindowActions.Close(w);
    }

    /// <summary>Wheel over an icon: cycle through an app's windows, or skip tracks on the Now Playing tile.</summary>
    void OnScrolled(DockItem item, int direction)
    {
        if (item.Kind == DockItemKind.Media)
        {
            if (direction > 0) Media.Next(); else Media.Previous();
            return;
        }
        if (item.Kind == DockItemKind.Harbor)
        {
            OnIconSizeWheel(direction);
            return;
        }
        if (item.Kind != DockItemKind.App) return;
        var stable = Tracker.Windows.Where(w => item.Windows.Contains(w.Hwnd) && Native.IsWindow(w.Hwnd))
            .OrderBy(w => w.FirstSeenTick).Select(w => w.Hwnd).ToList();
        if (stable.Count == 0) return;
        var fgRoot = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOTOWNER);
        int i = stable.IndexOf(fgRoot);
        int next = i < 0 ? 0 : (i + (direction > 0 ? -1 : 1) + stable.Count) % stable.Count;
        WindowActions.Activate(stable[next]);
    }

    void OnFlashed(TrackedWindow w)
    {
        Rebuild();
        var item = Items.FirstOrDefault(i => i.Windows.Contains(w.Hwnd));
        if (item != null) foreach (var dock in _docks) dock.Bounce(item, 2);
    }

    // ------------------------------------------------------------------ pinning

    public void Pin(DockItem item)
    {
        if (item.IsPinned || item.Kind != DockItemKind.App) return;
        string? path = item.AppId != null && (item.AppId.Contains('!') || item.ExePath == null || item.AppId == "Microsoft.Windows.Explorer")
            ? @"shell:AppsFolder\" + item.AppId
            : item.ExePath;
        if (path == null) return;
        _settings.Pinned.Add(new PinnedApp { Path = path, Name = item.Name, AppId = item.AppId });
        Save();
        Rebuild();
    }

    public void Pin(ShellInterop.InstalledApp app)
    {
        if (_settings.Pinned.Any(p => p.Path.Equals(app.ParsingName, StringComparison.OrdinalIgnoreCase))) return;
        var exe = AppCatalog.AppIdToPath(app.AppId);
        _settings.Pinned.Add(exe != null && File.Exists(exe)
            ? new PinnedApp { Path = exe, Name = app.Name }
            : new PinnedApp { Path = app.ParsingName, Name = app.Name, AppId = app.AppId });
        Save();
        Rebuild();
    }

    public void Unpin(DockItem item)
    {
        if (item.Pin == null) return;
        _settings.Pinned.Remove(item.Pin);
        Save();
        Rebuild();
    }

    void OnReordered(DockItem item, int newPinnedIndex)
    {
        if (item.Pin == null || newPinnedIndex < 0) return;
        _settings.Pinned.Remove(item.Pin);
        _settings.Pinned.Insert(Math.Min(newPinnedIndex, _settings.Pinned.Count), item.Pin);
        Save();
        Rebuild();
    }

    void OnIconSizeWheel(int direction)
    {
        _settings.Dock.IconSize = Math.Clamp(_settings.Dock.IconSize + direction * 4, DockLimits.MinIcon, DockLimits.MaxIcon);
        foreach (var dock in _docks) dock.ApplySettings(_settings.Dock);
        Save();
    }

    /// <summary>Tells listeners (the settings window) that settings changed somewhere else.</summary>
    public void NotifySettingsChanged() => SettingsChanged?.Invoke();

    void Save()
    {
        _settings.Save();
        SettingsChanged?.Invoke();
    }

    // ------------------------------------------------------------------ settings

    public void ApplySettings()
    {
        SetTaskbarHidden(_settings.Dock.HideWindowsTaskbar);
        SyncDocks();
        foreach (var dock in _docks) dock.ApplySettings(_settings.Dock);
        Rebuild();
        CheckOverlap();
        SettingsApplied?.Invoke();
    }

    // ------------------------------------------------------------------ Pomodoro button

    float _timerFraction = 1;
    bool _timerRunning, _timerOnBreak;

    void ApplyPomodoro(DockItem item)
    {
        item.TimerFraction = _timerFraction;
        item.TimerRunning = _timerRunning;
        item.TimerOnBreak = _timerOnBreak;
    }

    /// <summary>Repaints the button only when the ring would visibly move, which is about once every 20 seconds of a 25 minute session.</summary>
    public void SetPomodoro(float fraction, bool running, bool onBreak)
    {
        bool same = (int)(fraction * 72) == (int)(_timerFraction * 72) && running == _timerRunning && onBreak == _timerOnBreak;
        _timerFraction = fraction;
        _timerRunning = running;
        _timerOnBreak = onBreak;
        if (same || Items.FirstOrDefault(i => i.Kind == DockItemKind.Pomodoro) is not { } item) return;
        ApplyPomodoro(item);
        item.IconVersion++;
        foreach (var dock in _docks) dock.Refresh(item);
    }

    public void BouncePomodoro()
    {
        if (Items.FirstOrDefault(i => i.Kind == DockItemKind.Pomodoro) is { } item)
            foreach (var dock in _docks) dock.Bounce(item, 2);
    }

    void SetTaskbarHidden(bool hidden)
    {
        if (hidden == TaskbarController.IsHidden) return;
        if (hidden) TaskbarController.Hide();
        else TaskbarController.Restore();
        // The work area changes asynchronously; reposition once Explorer has caught up.
        foreach (var dock in _docks) dock.Reposition();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            foreach (var dock in _docks) dock.Reposition();
        };
        t.Start();
    }

    // ------------------------------------------------------------------ visibility rules

    void CheckFullscreen()
    {
        // Each dock asks about its own screen: a game full screen on one shouldn't hide the dock on the other.
        foreach (var dock in _docks)
            dock.SetFullscreenHidden(_settings.Dock.HideOnFullscreen && IsFullscreenForeground(dock.Handle));
    }

    /// <summary>True when the foreground window is a fullscreen app/game/video on the dock's monitor.</summary>
    public static bool IsFullscreenForeground(IntPtr dockHwnd)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == dockHwnd) return false;
        var cls = Native.GetWindowClass(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) return true; // D3D fullscreen, presentation
        // Maximized is never fullscreen, even for apps with custom title bars (no WS_CAPTION), which
        // otherwise look fullscreen when the taskbar is hidden and kept toggling the dock.
        if (Native.IsZoomed(fg)) return false;
        var monitor = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero) return false;
        // With a dock window, only fullscreen on the dock's own monitor matters.
        if (dockHwnd != IntPtr.Zero && monitor != Native.MonitorFromWindow(dockHwnd, Native.MONITOR_DEFAULTTOPRIMARY)) return false;
        // A resizable window is never fullscreen, even when it happens to fill the monitor.
        if ((Native.GetStyle(fg) & Native.WS_THICKFRAME) != 0) return false;
        var mi = Native.GetMonitorInfo(monitor);
        Native.GetWindowRect(fg, out var r);
        bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        if (!covers) return false;
        return true;
    }

    void CheckStartMenu()
    {
        // Get out of the way of Start and Search, then glide back when they close.
        bool open = WindowActions.IsStartMenuOpen();
        if (open == _startMenuHidden) return;
        _startMenuHidden = open;
        CheckOverlap();
    }

    void ScheduleOverlapCheck()
    {
        if (!_overlapTimer.IsEnabled) _overlapTimer.Start();
    }

    void CheckOverlap()
    {
        var mode = _settings.Dock.AutoHide;
        foreach (var dock in _docks)
        {
            bool hide = mode switch
            {
                AutoHideMode.Always => true,
                AutoHideMode.WhenOverlapped => AnyWindowOverlapsDock(dock),
                _ => false,
            };
            dock.SetAutoHideWanted(hide || _startMenuHidden);
        }
    }

    bool AnyWindowOverlapsDock(DockWindow dock)
    {
        var bar = dock.BarScreenRect;
        int pad = 4;
        foreach (var w in Tracker.Windows)
        {
            if (Native.IsIconic(w.Hwnd) || !Native.IsWindowVisible(w.Hwnd) || Native.IsCloaked(w.Hwnd)) continue;
            var r = Native.GetVisibleBounds(w.Hwnd);
            if (r.Right > bar.Left - pad && r.Left < bar.Right + pad && r.Bottom > bar.Top - pad && r.Top < bar.Bottom + pad) return true;
        }
        return false;
    }

    public void Dispose()
    {
        TaskbarController.Restore();
        Media.Dispose();
        _clockTimer.Stop();
        foreach (var dock in _docks.ToList()) dock.Dispose();
        _docks.Clear();
        Tracker.Dispose();
        Catalog.Dispose();
    }
}
