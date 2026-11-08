using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Harbor.Dock;
using Harbor.Interop;
using Harbor.Settings;
using Harbor.UI;
using Harbor.Wallpaper;
using Microsoft.Win32;

namespace Harbor;

public partial class App : Application
{
    const int WM_HARBOR_SHOW_SETTINGS = Native.WM_APP + 2;
    /// <summary>Sent by the installer before it replaces or removes the app, so the taskbar and desktop get restored.</summary>
    const int WM_HARBOR_QUIT = Native.WM_APP + 3;

    Mutex? _singleInstance;
    AppSettings _settings = null!;
    DockController? _dock;
    WallpaperController? _wallpaper;
    TrayIcon? _tray;
    SettingsWindow? _settingsWindow;
    PreviewController? _previews;
    bool _shuttingDown, _restarting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF only draws the settings window, menus and previews. Software drawing handles those fine, and keeps WPF
        // from loading a whole Direct3D 9 stack, which on current Intel drivers means Direct3D 12 and video as well.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--watch" && int.TryParse(e.Args[1], out int watched))
        {
            WatchForCrash(watched);
            Shutdown();
            return;
        }

        // Developer aid: harbor.exe --render-previews <folder> [seconds] writes a PNG still of every scene.
        if (e.Args.Length >= 2 && e.Args[0] == "--render-previews")
        {
            RenderPreviews(e.Args[1], e.Args.Length > 2 && float.TryParse(e.Args[2], out var t) ? t : 23.5f, e.Args.Length > 3 ? e.Args[3] : null);
            Shutdown();
            return;
        }

        // Developer aid: harbor.exe --test-preview <exe name> [tiles] [ms] [screens] shows the window preview for that app,
        // repeating its windows to make up the number of tiles, on each of a comma separated list of screens in turn.
        if (e.Args.Length >= 2 && e.Args[0] == "--test-preview")
        {
            ThemeManager.Apply(AppSettings.Load().Dock);
            TestPreview(e.Args[1], e.Args.Length > 2 && int.TryParse(e.Args[2], out int tiles) ? tiles : 0,
                e.Args.Length > 3 && int.TryParse(e.Args[3], out int ms) ? ms : 5000, e.Args.Length > 4 ? e.Args[4] : "0");
            return;
        }

        // For the installer: brings the taskbar back if Harbor was forced closed while hiding it.
        if (e.Args.Contains("--restore-taskbar"))
        {
            TaskbarController.RecoverFromCrash();
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\Harbor.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: ask that instance to show its settings instead.
            var tray = Native.FindWindow("HarborTray", null);
            if (tray != IntPtr.Zero) Native.PostMessage(tray, WM_HARBOR_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        StartCrashWatcher();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true; // keep the dock alive; the error is logged
            if (ex.Exception is OutOfMemoryException && ex.Exception.StackTrace?.Contains("DUCE") == true) RestartAfterDrawingFailure();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("Fatal exception", ex.ExceptionObject as Exception);
            TaskbarController.Restore();
        };

        Diag.Mark("runtime and WPF up");
        TaskbarController.RecoverFromCrash();
        _settings = AppSettings.Load();
        if (!_settings.FirstRunDone)
        {
            PinImporter.ImportTaskbarPins(_settings);
            _settings.FirstRunDone = true;
            _settings.Save();
        }
        ThemeManager.Apply(_settings.Dock);
        Diag.Mark("settings and theme");

        _dock = new DockController(_settings, Dispatcher);
        Diag.Mark("dock");
        _wallpaper = new WallpaperController(_settings, _dock.Tracker);
        Diag.Mark("wallpaper controller");
        _dock.Tracker.ExplorerRestarted += () => _wallpaper.Reattach();
        _dock.ContextMenuRequested += ShowDockMenu;
        _dock.OpenAppRequested += () => OpenSettings();
        _dock.CurrentWallpaperMode = () => _settings.Wallpaper.Mode;
        _dock.WallpaperModeChosen += SetWallpaperMode;
        _previews = new PreviewController(_dock) { Enabled = _settings.Dock.WindowPreviews };
        Diag.Mark("previews");
        _dock.SettingsChanged += () => _previews.Enabled = _settings.Dock.WindowPreviews;

        _tray = new TrayIcon();
        _tray.LeftClick += () => OpenSettings();
        _tray.RightClick += ShowTrayMenu;
        _tray.Message += msg =>
        {
            if (msg == WM_HARBOR_SHOW_SETTINGS) OpenSettings();
            else if (msg == WM_HARBOR_QUIT) Shutdown();
        };
        _wallpaper.StatusChanged += () => _tray.SetTooltip($"Harbor · wallpaper {_wallpaper.StatusText.ToLowerInvariant()}");

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        if (e.Args.Contains("--settings")) OpenSettings();
        RunDeveloperTests(e.Args);
        if (e.Args.Contains("--test-settings"))
        {
            // Developer aid: shows a settings page for a while without taking focus, then closes it.
            string page = e.Args.SkipWhile(a => a != "--test-settings").Skip(1).FirstOrDefault() ?? "home";
            int delay = int.TryParse(e.Args.SkipWhile(a => a != "--test-settings").Skip(2).FirstOrDefault(), out int seconds) ? seconds : 20;
            var open = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            open.Tick += (_, _) =>
            {
                open.Stop();
                OpenSettings(page, activate: false);
                if (_settingsWindow != null) _settingsWindow.Topmost = true; // visible over other apps, for a screenshot
                var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                close.Tick += (_, _) => { close.Stop(); _settingsWindow?.Close(); };
                close.Start();
            };
            open.Start();
        }
        Log.Info("Harbor started");
        Diag.Mark("started");
        ReleaseMemoryAfter(TimeSpan.FromSeconds(12));
    }

    /// <summary>
    /// Startup and the settings window use a lot of memory briefly, then nothing touches it again. A dock sits in
    /// the background for days, so give it back rather than waiting for Windows to run short.
    /// </summary>
    void ReleaseMemoryAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Diag.Mark("before releasing");
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
            Diag.Mark("after releasing");
        };
        timer.Start();
    }

    /// <summary>
    /// Developer aids, none of which take focus. --test-panel and --test-calendar [ms] [screen] hold a panel open;
    /// --test-menu [screen] opens a menu over that screen's Harbor icon; --test-screens turns "all screens" off and on again.
    /// Each logs what it did, along with where every dock is.
    /// </summary>
    void RunDeveloperTests(string[] args)
    {
        string? Arg(string flag, int position) => args.SkipWhile(a => a != flag).Skip(position).FirstOrDefault();
        int Number(string flag, int position, int fallback) => int.TryParse(Arg(flag, position), out int n) ? n : fallback;
        DockWindow Screen(int index) => _dock!.Docks[Math.Clamp(index, 0, _dock.Docks.Count - 1)];
        void After(double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
            timer.Start();
        }
        void DescribeDocks() { foreach (var dock in _dock!.Docks) Log.Info(dock.DescribeForTest()); }

        if (args.Any(a => a.StartsWith("--test-"))) After(1.5, DescribeDocks);
        if (args.Contains("--test-panel"))
            After(2, () => Screen(Number("--test-panel", 2, 0)).ShowPanelForTest(Number("--test-panel", 1, 9000), DockItemKind.Harbor));
        if (args.Contains("--test-calendar"))
            After(2, () => Screen(Number("--test-calendar", 2, 0)).ShowPanelForTest(Number("--test-calendar", 1, 9000), DockItemKind.Clock));
        if (args.Contains("--test-menu"))
            After(2, () =>
            {
                var dock = Screen(Number("--test-menu", 1, 0));
                var item = _dock!.Items.First(i => i.Kind == DockItemKind.Harbor);
                var menu = new ContextMenu();
                menu.Items.Add(Menus.Item("Open Harbor", () => { }, "\uE8A7"));
                menu.Items.Add(Menus.Item("Hide this button", () => { }, "\uE711"));
                menu.Items.Add(Menus.Item("Quit Harbor", () => { }, "\uE7E8"));
                var anchor = dock.GetItemScreenRect(item);
                dock.SetMenuOpen(true);
                Menus.Show(menu, () => dock.SetMenuOpen(false), anchor, takeFocus: false);
                After(0.6, () =>
                {
                    if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src)
                    {
                        Native.GetWindowRect(src.Handle, out var r);
                        Log.Info($"Menu test: dpi={Native.GetDpiForWindow(src.Handle)} ml={r.Left} mt={r.Top} mr={r.Right} mb={r.Bottom} al={anchor?.Left} at={anchor?.Top} ar={anchor?.Right} ab={anchor?.Bottom}");
                    }
                });
                After(Number("--test-menu", 2, 2000) / 1000.0, () => menu.IsOpen = false);
            });
        if (args.Contains("--test-restart")) After(2, RestartAfterDrawingFailure);
        if (args.Contains("--test-screens"))
            After(3, () =>
            {
                Log.Info($"Screens test: {_dock!.Docks.Count} docks");
                _settings.Dock.ShowOnAllScreens = false;
                _dock.ApplySettings();
                Log.Info($"Screens test: main screen only, {_dock.Docks.Count} docks");
                DescribeDocks();
                After(3, () =>
                {
                    _settings.Dock.ShowOnAllScreens = true;
                    _dock.ApplySettings();
                    Log.Info($"Screens test: all screens, {_dock.Docks.Count} docks");
                    DescribeDocks();
                });
            });
    }

    static void StartCrashWatcher()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--watch {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Could not start the crash watcher: " + ex.Message); }
    }

    /// <summary>
    /// Runs in a small second process and does nothing until Harbor ends. A crash can happen while the Windows taskbar is hidden,
    /// and a process that has crashed can't put it back, so this does: it restores the taskbar and starts Harbor again. Quitting
    /// and ending the task in Task Manager are both left alone, and a Harbor that keeps dying within seconds isn't restarted.
    /// </summary>
    static void WatchForCrash(int pid)
    {
        System.Diagnostics.Process target;
        try
        {
            target = System.Diagnostics.Process.GetProcessById(pid);
            _ = target.Handle; // the exit code can only be read later if this is opened now
        }
        catch { return; }
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
        target.WaitForExit();
        uint code = unchecked((uint)target.ExitCode);
        bool crashed = (code & 0xF0000000) is 0xC0000000 or 0xE0000000 || code is 0x80131506 or 0x80131623;
        if (!crashed) return;
        Log.Warn($"Harbor crashed (exit code 0x{code:X8})");
        TaskbarController.RecoverFromCrash();
        if ((target.ExitTime - target.StartTime).TotalSeconds >= 30 && Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(exe, "--recovered")?.Dispose();
    }

    /// <summary>
    /// When the PC runs very short of memory, WPF's link to its drawing thread can fail, and it never recovers: every menu and
    /// window fails from then on. A fresh start is the only cure. The wait stops a PC that's still short of memory from looping.
    /// </summary>
    void RestartAfterDrawingFailure()
    {
        if (_restarting) return;
        _restarting = true;
        double uptime = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds;
        Log.Warn("WPF can no longer draw; restarting Harbor");
        var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, 45 - uptime)) };
        wait.Tick += (_, _) => { wait.Stop(); Shutdown(); };
        wait.Start();
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(_settings.Dock));
    }

    static void RenderPreviews(string folder, float time, string? only)
    {
        Directory.CreateDirectory(folder);
        foreach (var scene in SceneCatalog.All.Where(s => !s.UsesVideo && (only == null || s.Id == only)))
        {
            const int w = 1280, h = 800;
            PreviewRenderer.RenderEach(new[] { scene.Id }, w, h, time, (id, px) =>
            {
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(folder, id + ".png"));
                enc.Save(fs);
            });
        }
    }

    void TestPreview(string exeName, int tiles, int ms, string screens)
    {
        var item = new DockItem("test", DockItemKind.App) { Name = exeName };
        Native.EnumWindows((h, _) =>
        {
            if (!WindowTracker.IsTaskbarWindow(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            var path = Native.GetProcessPath(pid);
            if (path != null && Path.GetFileNameWithoutExtension(path).Equals(exeName, StringComparison.OrdinalIgnoreCase)) item.Windows.Add(h);
            return true;
        }, IntPtr.Zero);
        for (int i = 0, found = item.Windows.Count; found > 0 && item.Windows.Count < tiles; i++) item.Windows.Add(item.Windows[i % found]);
        var monitors = Native.AllMonitors();
        var preview = new WindowPreview();
        var queue = new Queue<int>(screens.Split(',').Select(int.Parse));
        void ShowNext()
        {
            var mi = Native.GetMonitorInfo(monitors[Math.Clamp(queue.Dequeue(), 0, monitors.Count - 1)]);
            int cx = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2, bottom = mi.rcWork.Bottom - 40;
            preview.ShowFor(item, new Native.RECT(cx - 40, bottom - 80, cx + 40, bottom));
            Log.Info($"Test preview for {exeName}: {preview.Describe()}");
        }
        ShowNext();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            if (queue.Count > 0) { ShowNext(); return; }
            t.Stop();
            preview.Close();
            Shutdown();
        };
        t.Start();
    }

    // ------------------------------------------------------------------ settings window

    void OpenSettings(string page = "home", bool activate = true)
    {
        if (_dock == null || _wallpaper == null) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, _dock, _wallpaper) { ShowActivated = activate };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ReleaseMemoryAfter(TimeSpan.FromSeconds(2));
            };
            _settingsWindow.Show();
        }
        _settingsWindow.ShowPage(page);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        if (!activate) return;
        WindowActions.TapKey(0xE8);
        _settingsWindow.Activate();
    }

    // ------------------------------------------------------------------ menus

    void ShowTrayMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Menus.Label("Harbor"));
        menu.Items.Add(new Separator());
        AddWallpaperItems(menu);
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("Hide Windows taskbar", ToggleTaskbar, "", _settings.Dock.HideWindowsTaskbar));
        menu.Items.Add(Menus.Item("Settings…", () => OpenSettings(), ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menus.Item("Quit Harbor", () => Shutdown(), ""));
        Menus.Show(menu);
    }

    void AddWallpaperItems(ContextMenu menu)
    {
        var w = _settings.Wallpaper;
        menu.Items.Add(Menus.Label("Wallpaper: " + (_wallpaper?.StatusText ?? "off")));
        menu.Items.Add(Menus.Item("Smart", () => SetWallpaperMode(WallpaperMode.Smart), "", w.Enabled && w.Mode == WallpaperMode.Smart));
        menu.Items.Add(Menus.Item("Always animated", () => SetWallpaperMode(WallpaperMode.Animated), "", w.Enabled && w.Mode == WallpaperMode.Animated));
        menu.Items.Add(Menus.Item("Static (saves battery)", () => SetWallpaperMode(WallpaperMode.Static), "", w.Enabled && w.Mode == WallpaperMode.Static));
        menu.Items.Add(Menus.Item("Choose wallpaper…", () => OpenSettings("wallpaper"), ""));
    }

    void SetWallpaperMode(WallpaperMode mode)
    {
        _settings.Wallpaper.Mode = mode;
        _settings.Wallpaper.Enabled = true;
        _settings.Save();
        _wallpaper?.Apply();
        _dock?.NotifySettingsChanged();
    }

    void ToggleTaskbar()
    {
        _settings.Dock.HideWindowsTaskbar = !_settings.Dock.HideWindowsTaskbar;
        _settings.Save();
        _dock?.ApplySettings();
    }

    void ShowDockMenu(DockWindow source, DockItem? item, Native.POINT pt)
    {
        if (_dock == null) return;
        var dock = _dock;
        var menu = new ContextMenu();

        switch (item?.Kind)
        {
            case DockItemKind.App:
                BuildAppMenu(menu, item);
                break;
            case DockItemKind.Media:
            {
                var media = dock.Media;
                menu.Items.Add(Menus.Label(media.Label));
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item(media.IsPlaying ? "Pause" : "Play", media.PlayPause, media.IsPlaying ? "\uE769" : "\uE768"));
                menu.Items.Add(Menus.Item("Next track", media.Next, "\uE893"));
                menu.Items.Add(Menus.Item("Previous track", media.Previous, "\uE892"));
                if (media.SourceAppId != null)
                    menu.Items.Add(Menus.Item("Open player", () => ShellInterop.ActivatePackagedApp(media.SourceAppId), "\uE8A7"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item("Hide Now Playing", () =>
                {
                    _settings.Dock.ShowNowPlaying = false;
                    _settings.Save();
                    dock.Rebuild();
                }, "\uE711"));
                break;
            }
            case DockItemKind.Harbor:
                menu.Items.Add(Menus.Item("Open Harbor", () => OpenSettings(), "\uE8A7"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item("Hide this button", () =>
                {
                    _settings.Dock.ShowHarborButton = false;
                    _settings.Save();
                    dock.Rebuild();
                    dock.NotifySettingsChanged();
                }, "\uE711"));
                menu.Items.Add(Menus.Item("Quit Harbor", () => Shutdown(), "\uE7E8"));
                break;
            case DockItemKind.Clock:
                menu.Items.Add(Menus.Item("Date and time settings", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:dateandtime") { UseShellExecute = true }), "\uE787"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item("Hide the clock", () =>
                {
                    _settings.Dock.ShowClock = false;
                    _settings.Save();
                    dock.Rebuild();
                    dock.NotifySettingsChanged();
                }, "\uE711"));
                break;
            case DockItemKind.RecycleBin:
                menu.Items.Add(Menus.Item("Open", () => System.Diagnostics.Process.Start("explorer.exe", "shell:RecycleBinFolder"), ""));
                menu.Items.Add(Menus.Item("Empty Recycle Bin", () => SHEmptyRecycleBin(IntPtr.Zero, null, 0), ""));
                break;
            default:
                if (item?.Kind == DockItemKind.Start)
                {
                    menu.Items.Add(Menus.Item("Open Start", WindowActions.OpenStartMenu, ""));
                    menu.Items.Add(new Separator());
                }
                AddWallpaperItems(menu);
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item("Magnification", () =>
                {
                    _settings.Dock.Magnification = !_settings.Dock.Magnification;
                    _settings.Save();
                    dock.ApplySettings();
                }, "", _settings.Dock.Magnification));
                menu.Items.Add(Menus.Item("Auto-hide when covered", () =>
                {
                    _settings.Dock.AutoHide = _settings.Dock.AutoHide == AutoHideMode.Never ? AutoHideMode.WhenOverlapped : AutoHideMode.Never;
                    _settings.Save();
                    dock.ApplySettings();
                }, "", _settings.Dock.AutoHide != AutoHideMode.Never));
                menu.Items.Add(Menus.Item("Hide Windows taskbar", ToggleTaskbar, "", _settings.Dock.HideWindowsTaskbar));
                menu.Items.Add(Menus.Item("Dock settings…", () => OpenSettings(), ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item("Quit Harbor", () => Shutdown(), ""));
                break;
        }

        source.SetMenuOpen(true);
        Native.RECT? anchor = item != null ? source.GetItemScreenRect(item) : null;
        if (anchor is { } r) anchor = new Native.RECT(r.Left, r.Top - (int)(30 * source.Scale), r.Right, r.Bottom);
        Menus.Show(menu, () => source.SetMenuOpen(false), anchor);
    }

    void BuildAppMenu(ContextMenu menu, DockItem item)
    {
        var dock = _dock!;
        var windows = item.Windows.Where(Native.IsWindow).ToList();
        if (windows.Count > 1)
        {
            foreach (var w in windows.Take(12))
            {
                string title = Native.GetWindowTitle(w);
                if (title.Length > 60) title = title[..57] + "…";
                menu.Items.Add(Menus.Item(string.IsNullOrWhiteSpace(title) ? item.Name : title, () => WindowActions.Activate(w), ""));
            }
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Menus.Item(item.IsRunning ? "New window" : "Open", () => dock.Launch(item), item.IsRunning ? "" : ""));
        string? exe = item.ExePath ?? (item.Pin?.Path is { } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null);
        if (exe != null)
            menu.Items.Add(Menus.Item("Run as administrator", () => dock.Launch(item, asAdmin: true), ""));
        menu.Items.Add(new Separator());

        if (item.IsPinned) menu.Items.Add(Menus.Item("Remove from Dock", () => dock.Unpin(item), ""));
        else menu.Items.Add(Menus.Item("Keep in Dock", () => dock.Pin(item), ""));
        if (exe != null && File.Exists(exe))
            menu.Items.Add(Menus.Item("Show in File Explorer", () => dock.OpenFileLocation(item), ""));

        if (windows.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Item(windows.Count > 1 ? $"Close all windows ({windows.Count})" : "Close window", () => dock.CloseAll(item), ""));
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    // ------------------------------------------------------------------ shutdown

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shuttingDown)
        {
            _shuttingDown = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            try { _settingsWindow?.Close(); } catch { }
            _previews?.Shutdown();
            _wallpaper?.Dispose();
            _dock?.Dispose();
            _tray?.Dispose();
            TaskbarController.Restore();
            Log.Info("Harbor exited");
        }
        _singleInstance?.Dispose();
        if (_restarting && Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe, "--recovered");
        base.OnExit(e);
    }
}
