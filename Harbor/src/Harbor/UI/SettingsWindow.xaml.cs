using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Harbor.Dock;
using Harbor.Interop;
using Harbor.Settings;
using Harbor.Wallpaper;
using Microsoft.Win32;
using HThemeMode = Harbor.Settings.ThemeMode;
using Path = System.IO.Path;

namespace Harbor.UI;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly DockController _dock;
    readonly WallpaperController _wallpaper;
    readonly DispatcherTimer _saveTimer;
    readonly ObservableCollection<PinRow> _pins = new();
    readonly ObservableCollection<SceneCard> _scenes = new();
    readonly Dictionary<string, ImageSource> _stills = new();
    List<AppRow> _allApps = new();
    LivePreview? _live;
    bool _loading = true; // sliders raise ValueChanged while InitializeComponent sets Minimum/Maximum

    internal SettingsWindow(AppSettings settings, DockController dock, WallpaperController wallpaper)
    {
        _settings = settings;
        _dock = dock;
        _wallpaper = wallpaper;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            ThemeManager.StyleWindow(this, mica: true);
            if (!ThemeManager.MicaSupported) SetResourceReference(BackgroundProperty, "H.Window");
        };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            UpdateLivePreview();
        };
        Hero.SizeChanged += (_, e) => HeroClip.Rect = new Rect(e.NewSize);
        DockStageImage.SizeChanged += (_, e) => StageClip.Rect = new Rect(e.NewSize);
        IsVisibleChanged += (_, _) => UpdateLivePreview();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };

        var version = typeof(App).Assembly.GetName().Version;
        AboutVersion.Text = $"Version {version?.ToString(3)}. A floating dock and live wallpaper for Windows.";
        SettingsPath.Text = AppSettings.Directory;
        // An .ico holds several sizes and WPF takes the first, which is too small for a high DPI screen.
        var icons = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/Harbor.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        AboutIcon.Source = icons.Frames.OrderByDescending(f => f.PixelWidth).First();

        PinnedList.ItemsSource = _pins;
        SceneGallery.ItemsSource = _scenes;
        LoadValues();
        RefreshPins();
        BuildSceneCards();
        LoadAllApps();
        RefreshMiniDocks();
        UpdateHero();

        _wallpaper.StatusChanged += UpdateStatus;
        _dock.Catalog.CatalogLoaded += LoadAllApps;
        _dock.SettingsChanged += OnDockSettingsChanged;
        // Dock icons load in the background; redraw the miniatures as they arrive.
        var iconRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        iconRefresh.Tick += (_, _) => { iconRefresh.Stop(); RefreshMiniDocks(); };
        Action<string> onIcon = _ => { iconRefresh.Stop(); iconRefresh.Start(); };
        _dock.Catalog.IconLoaded += onIcon;
        Closed += (_, _) => _dock.Catalog.IconLoaded -= onIcon;
        Closed += (_, _) =>
        {
            _live?.Dispose();
            _wallpaper.StatusChanged -= UpdateStatus;
            _dock.Catalog.CatalogLoaded -= LoadAllApps;
            _dock.SettingsChanged -= OnDockSettingsChanged;
            if (_saveTimer.IsEnabled) _settings.Save();
        };
        UpdateStatus();
    }

    void OnDockSettingsChanged()
    {
        LoadValues();
        RefreshPins();
        RefreshMiniDocks();
    }

    public void ShowPage(string page)
    {
        (page switch
        {
            "dock" => NavDock,
            "apps" => NavApps,
            "wallpaper" => NavWallpaper,
            "power" => NavPower,
            "about" => NavAbout,
            _ => NavHome,
        }).IsChecked = true;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome == null) return;
        var pages = new (RadioButton nav, FrameworkElement page)[]
        {
            (NavHome, PageHome), (NavDock, PageDock), (NavApps, PageApps), (NavWallpaper, PageWallpaper), (NavPower, PagePower), (NavAbout, PageAbout),
        };
        foreach (var (nav, page) in pages)
        {
            bool show = nav.IsChecked == true;
            page.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && ThemeManager.AnimationsEnabled)
            {
                // One quiet transition per navigation: the page settles in place.
                page.Opacity = 0;
                page.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            }
        }
        Scroller.ScrollToTop();
        UpdateLivePreview();
    }

    // ------------------------------------------------------------------ live hero

    void UpdateLivePreview()
    {
        if (PageHome == null) return;
        // The preview follows the same power rules as the wallpaper: on battery outside Full, show a still.
        bool powerOk = !_wallpaper.OnBattery || _wallpaper.Tier == WallpaperTier.Full;
        bool want = IsVisible && WindowState != WindowState.Minimized && PageHome.Visibility == Visibility.Visible && powerOk;
        var scene = SceneCatalog.Get(_settings.Wallpaper.Scene);
        if (want && !scene.UsesVideo)
        {
            _live ??= new LivePreview(Dispatcher, scene.Id, 640, 360);
            HeroImage.Source = _live.Bitmap;
            _live.Scene = scene.Id;
            _live.Start();
        }
        else
        {
            _live?.Stop();
            if (_stills.TryGetValue(scene.UsesVideo ? "aurora" : scene.Id, out var still)) HeroImage.Source = still;
        }
    }

    void UpdateHero()
    {
        var scene = SceneCatalog.Get(_settings.Wallpaper.Scene);
        HeroSceneName.Text = scene.Name;
        HeroSceneDesc.Text = scene.UsesVideo && !string.IsNullOrEmpty(_settings.Wallpaper.VideoPath)
            ? Path.GetFileName(_settings.Wallpaper.VideoPath)
            : scene.Description;
        if (_stills.TryGetValue(scene.Id, out var still)) DockStageImage.Source = still;
        UpdateLivePreview();
    }

    System.Numerics.Vector2 HeroUv(MouseEventArgs e)
    {
        var p = e.GetPosition(HeroImage);
        // The image is UniformToFill: map through the cropped area so the cursor lines up with the scene.
        double iw = 16, ih = 9, w = HeroImage.ActualWidth, h = HeroImage.ActualHeight;
        double scale = Math.Max(w / iw, h / ih);
        double ox = (w - iw * scale) / 2, oy = (h - ih * scale) / 2;
        return new System.Numerics.Vector2((float)((p.X - ox) / (iw * scale)), (float)((p.Y - oy) / (ih * scale)));
    }

    void Hero_MouseMove(object sender, MouseEventArgs e) => _live?.SetPointer(HeroUv(e));
    void Hero_MouseLeave(object sender, MouseEventArgs e) => _live?.SetPointer(null);
    void Hero_Click(object sender, MouseButtonEventArgs e) => _live?.Click(HeroUv(e));

    // ------------------------------------------------------------------ mini docks

    void RefreshMiniDocks()
    {
        var icons = _dock.Items
            .Where(i => i.Kind is DockItemKind.App or DockItemKind.Start or DockItemKind.RecycleBin or DockItemKind.Media or DockItemKind.Harbor)
            .Select(i => i.Kind switch
            {
                DockItemKind.Start => MiniDock.StartMarker,
                DockItemKind.Harbor => HarborIcon,
                _ => ToBitmap(i.Icon),
            })
            .ToList();
        var d = _settings.Dock;
        foreach (var mini in new[] { HeroDock, DockPreview })
        {
            mini.IconSize = mini == HeroDock ? Math.Min(d.IconSize, 40) : Math.Min(d.IconSize, 56);
            mini.Magnification = d.Magnification ? d.MagnificationAmount : 0;
            mini.Roundness = d.CornerRoundness;
            mini.TintOpacity = d.TintOpacity;
            mini.Dark = ThemeManager.IsDark;
            mini.SetIcons(icons);
        }
    }

    // ------------------------------------------------------------------ values

    void LoadValues()
    {
        _loading = true;
        var d = _settings.Dock;
        IconSize.Value = d.IconSize;
        Magnify.IsChecked = d.Magnification;
        MagnifyAmount.Value = d.MagnificationAmount;
        ThemeSystem.IsChecked = d.Theme == HThemeMode.System;
        ThemeDark.IsChecked = d.Theme == HThemeMode.Dark;
        ThemeLight.IsChecked = d.Theme == HThemeMode.Light;
        Tint.Value = d.TintOpacity;
        Roundness.Value = d.CornerRoundness;
        HideNever.IsChecked = d.AutoHide == AutoHideMode.Never;
        HideOverlap.IsChecked = d.AutoHide == AutoHideMode.WhenOverlapped;
        HideAlways.IsChecked = d.AutoHide == AutoHideMode.Always;
        HideTaskbar.IsChecked = QuickTaskbar.IsChecked = d.HideWindowsTaskbar;
        HideFullscreen.IsChecked = d.HideOnFullscreen;
        Previews.IsChecked = QuickPreviews.IsChecked = d.WindowPreviews;
        Bounce.IsChecked = d.BounceOnLaunch;
        Labels.IsChecked = d.ShowLabels;
        ShowHarbor.IsChecked = d.ShowHarborButton;
        ShowClockToggle.IsChecked = d.ShowClock;
        ShowAllScreens.IsChecked = d.ShowOnAllScreens;
        ShowStart.IsChecked = d.ShowStartButton;
        ShowMedia.IsChecked = d.ShowNowPlaying;
        ShowBin.IsChecked = d.ShowRecycleBin;
        Indicators.IsChecked = d.ShowRunningIndicators;

        var w = _settings.Wallpaper;
        WpEnabled.IsChecked = w.Enabled;
        HomeSmart.IsChecked = ModeSmart.IsChecked = w.Mode == WallpaperMode.Smart;
        HomeAnimated.IsChecked = ModeAnimated.IsChecked = w.Mode == WallpaperMode.Animated;
        HomeStatic.IsChecked = ModeStatic.IsChecked = w.Mode == WallpaperMode.Static;
        WpInteractive.IsChecked = QuickPointer.IsChecked = w.Interactive;
        WpMusic.IsChecked = QuickMusic.IsChecked = w.AudioReactive;
        WpStrength.Value = w.MouseStrength;
        WpSpeed.Value = w.Speed;
        WpHue.Value = w.Hue;
        WpSat.Value = w.Saturation;
        WpBright.Value = w.Brightness;
        BatEco.IsChecked = w.OnBattery == BatteryBehavior.Eco;
        BatStatic.IsChecked = w.OnBattery == BatteryBehavior.Static;
        BatFull.IsChecked = w.OnBattery == BatteryBehavior.Full;
        BatLimit.Value = w.StaticBelowBatteryPercent;
        WpSaver.IsChecked = w.StaticOnEnergySaver;
        Idle0.IsChecked = w.IdleMinutes <= 0;
        Idle2.IsChecked = w.IdleMinutes is > 0 and <= 2;
        Idle5.IsChecked = w.IdleMinutes is > 2 and <= 5;
        Idle15.IsChecked = w.IdleMinutes > 5;
        WpCovered.IsChecked = w.PauseWhenCovered;
        WpFullscreen.IsChecked = w.PauseWhenFullscreen;
        WpLocked.IsChecked = w.PauseWhenLocked;
        Fps30.IsChecked = w.FrameRateLimit <= 30;
        Fps60.IsChecked = w.FrameRateLimit is > 30 and <= 60;
        Fps120.IsChecked = w.FrameRateLimit > 60;
        Amb15.IsChecked = w.AmbientFrameRate <= 15;
        Amb24.IsChecked = w.AmbientFrameRate is > 15 and <= 24;
        Amb30.IsChecked = w.AmbientFrameRate is > 24 and <= 30;
        Amb60.IsChecked = w.AmbientFrameRate > 30;
        Res100.IsChecked = w.RenderScale >= 0.99;
        Res75.IsChecked = w.RenderScale is >= 0.6 and < 0.99;
        Res50.IsChecked = w.RenderScale < 0.6;
        if (!string.IsNullOrEmpty(w.VideoPath)) VideoPathText.Text = w.VideoPath;

        Autostart.IsChecked = Startup.IsEnabled;
        _loading = false;
        UpdateValueLabels();
    }

    void UpdateValueLabels()
    {
        IconSizeValue.Text = $"{IconSize.Value:0}";
        MagnifyValue.Text = $"{1 + MagnifyAmount.Value:0.0#}x";
        TintValue.Text = $"{Tint.Value * 100:0}%";
        RoundValue.Text = $"{Roundness.Value * 100:0}%";
        StrengthValue.Text = $"{WpStrength.Value * 100:0}%";
        SpeedValue.Text = $"{WpSpeed.Value:0.0#}x";
        HueValue.Text = $"{WpHue.Value:0}°";
        SatValue.Text = $"{WpSat.Value * 100:0}%";
        BrightValue.Text = $"{WpBright.Value * 100:0}%";
        BatLimitValue.Text = BatLimit.Value < 1 ? "Off" : $"{BatLimit.Value:0}%";
        MagnifyAmount.IsEnabled = Magnify.IsChecked == true;
    }

    void Dock_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var d = _settings.Dock;
        d.IconSize = Math.Round(IconSize.Value);
        d.Magnification = Magnify.IsChecked == true;
        d.MagnificationAmount = Math.Round(MagnifyAmount.Value, 2);
        d.Theme = ThemeDark.IsChecked == true ? HThemeMode.Dark : ThemeLight.IsChecked == true ? HThemeMode.Light : HThemeMode.System;
        d.TintOpacity = Math.Round(Tint.Value, 2);
        d.CornerRoundness = Math.Round(Roundness.Value, 2);
        d.AutoHide = HideAlways.IsChecked == true ? AutoHideMode.Always : HideOverlap.IsChecked == true ? AutoHideMode.WhenOverlapped : AutoHideMode.Never;
        d.HideWindowsTaskbar = HideTaskbar.IsChecked == true;
        d.HideOnFullscreen = HideFullscreen.IsChecked == true;
        d.WindowPreviews = Previews.IsChecked == true;
        d.BounceOnLaunch = Bounce.IsChecked == true;
        d.ShowLabels = Labels.IsChecked == true;
        d.ShowHarborButton = ShowHarbor.IsChecked == true;
        d.ShowClock = ShowClockToggle.IsChecked == true;
        d.ShowOnAllScreens = ShowAllScreens.IsChecked == true;
        d.ShowStartButton = ShowStart.IsChecked == true;
        d.ShowNowPlaying = ShowMedia.IsChecked == true;
        d.ShowRecycleBin = ShowBin.IsChecked == true;
        d.ShowRunningIndicators = Indicators.IsChecked == true;
        _loading = true;
        QuickTaskbar.IsChecked = d.HideWindowsTaskbar;
        QuickPreviews.IsChecked = d.WindowPreviews;
        _loading = false;
        UpdateValueLabels();
        _dock.ApplySettings();
        if (sender == ThemeDark || sender == ThemeLight || sender == ThemeSystem)
        {
            ThemeManager.Apply(d);
            ThemeManager.StyleWindow(this, mica: true);
        }
        RefreshMiniDocks();
        QueueSave();
    }

    void Wallpaper_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var w = _settings.Wallpaper;
        w.Enabled = WpEnabled.IsChecked == true;
        w.Mode = ModeStatic.IsChecked == true ? WallpaperMode.Static : ModeAnimated.IsChecked == true ? WallpaperMode.Animated : WallpaperMode.Smart;
        w.Interactive = WpInteractive.IsChecked == true;
        w.AudioReactive = WpMusic.IsChecked == true;
        w.MouseStrength = Math.Round(WpStrength.Value, 2);
        w.Speed = Math.Round(WpSpeed.Value, 2);
        w.Hue = Math.Round(WpHue.Value);
        w.Saturation = Math.Round(WpSat.Value, 2);
        w.Brightness = Math.Round(WpBright.Value, 2);
        w.OnBattery = BatStatic.IsChecked == true ? BatteryBehavior.Static : BatFull.IsChecked == true ? BatteryBehavior.Full : BatteryBehavior.Eco;
        w.StaticBelowBatteryPercent = (int)Math.Round(BatLimit.Value / 5) * 5;
        w.StaticOnEnergySaver = WpSaver.IsChecked == true;
        w.IdleMinutes = Idle2.IsChecked == true ? 2 : Idle5.IsChecked == true ? 5 : Idle15.IsChecked == true ? 15 : 0;
        w.PauseWhenCovered = WpCovered.IsChecked == true;
        w.PauseWhenFullscreen = WpFullscreen.IsChecked == true;
        w.PauseWhenLocked = WpLocked.IsChecked == true;
        w.FrameRateLimit = Fps30.IsChecked == true ? 30 : Fps120.IsChecked == true ? 120 : 60;
        w.AmbientFrameRate = Amb15.IsChecked == true ? 15 : Amb24.IsChecked == true ? 24 : Amb60.IsChecked == true ? 60 : 30;
        w.RenderScale = Res50.IsChecked == true ? 0.5 : Res75.IsChecked == true ? 0.75 : 1.0;
        SyncMirrors();
        UpdateValueLabels();
        _wallpaper.Apply();
        QueueSave();
    }

    /// <summary>Home repeats a few controls from other pages; keep both copies in step.</summary>
    void SyncMirrors()
    {
        _loading = true;
        var w = _settings.Wallpaper;
        HomeSmart.IsChecked = ModeSmart.IsChecked = w.Mode == WallpaperMode.Smart;
        HomeAnimated.IsChecked = ModeAnimated.IsChecked = w.Mode == WallpaperMode.Animated;
        HomeStatic.IsChecked = ModeStatic.IsChecked = w.Mode == WallpaperMode.Static;
        QuickPointer.IsChecked = WpInteractive.IsChecked = w.Interactive;
        QuickMusic.IsChecked = WpMusic.IsChecked = w.AudioReactive;
        _loading = false;
    }

    void HomeMode_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Wallpaper.Mode = HomeStatic.IsChecked == true ? WallpaperMode.Static : HomeAnimated.IsChecked == true ? WallpaperMode.Animated : WallpaperMode.Smart;
        _settings.Wallpaper.Enabled = true;
        WpEnabled.IsChecked = true;
        SyncMirrors();
        _wallpaper.Apply();
        QueueSave();
    }

    void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Wallpaper.Interactive = QuickPointer.IsChecked == true;
        _settings.Wallpaper.AudioReactive = QuickMusic.IsChecked == true;
        _settings.Dock.WindowPreviews = QuickPreviews.IsChecked == true;
        _settings.Dock.HideWindowsTaskbar = QuickTaskbar.IsChecked == true;
        _loading = true;
        Previews.IsChecked = _settings.Dock.WindowPreviews;
        HideTaskbar.IsChecked = _settings.Dock.HideWindowsTaskbar;
        _loading = false;
        SyncMirrors();
        _wallpaper.Apply();
        _dock.ApplySettings();
        QueueSave();
    }

    void ResetColor_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        WpHue.Value = 0; WpSat.Value = 1; WpBright.Value = 1;
        _loading = false;
        Wallpaper_Changed(sender, e);
    }

    void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void UpdateStatus()
    {
        string key = _wallpaper.StatusText switch
        {
            "Animated" => "H.StatusAnimated",
            "Eco" => "H.StatusEco",
            "Static" => "H.StatusStatic",
            "Error" => "H.Danger",
            _ => "H.StatusPaused",
        };
        foreach (var dot in new[] { StatusDot, HeroPillDot, PowerDot }) dot.SetResourceReference(Shape.FillProperty, key);
        StatusText.Text = HeroPillText.Text = PowerTitle.Text = HomeNowTitle.Text = _wallpaper.StatusText;
        PowerDetail.Text = HomeNowDetail.Text = _wallpaper.StatusDetail;
        StatusSub.Text = _wallpaper.OnBattery ? $"On battery, {_wallpaper.BatteryPercent}%" : "Plugged in";
        UpdateLivePreview();
    }

    // ------------------------------------------------------------------ scenes

    void BuildSceneCards()
    {
        _scenes.Clear();
        foreach (var info in SceneCatalog.All)
            _scenes.Add(new SceneCard(info) { IsSelected = info.Id == _settings.Wallpaper.Scene });

        // Real stills of each scene, rendered off the UI thread with the actual shaders.
        var ui = Dispatcher;
        var cards = _scenes.Where(c => !c.Info.UsesVideo).ToDictionary(c => c.Info.Id);
        Task.Run(() => PreviewRenderer.RenderEach(cards.Keys, 400, 225, 23.5f, (id, px) => ui.BeginInvoke(() =>
        {
            var bmp = BitmapSource.Create(400, 225, 96, 96, PixelFormats.Bgra32, null, px, 400 * 4);
            bmp.Freeze();
            cards[id].Preview = bmp;
            _stills[id] = bmp;
            if (id == _settings.Wallpaper.Scene)
            {
                DockStageImage.Source = bmp;
                if (HeroImage.Source == null) HeroImage.Source = bmp;
            }
        })));
    }

    void Scene_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SceneCard card) return;
        if (card.Info.UsesVideo && string.IsNullOrEmpty(_settings.Wallpaper.VideoPath) && !PickVideo()) return;
        foreach (var c in _scenes) c.IsSelected = c == card;
        _settings.Wallpaper.Scene = card.Info.Id;
        if (!_settings.Wallpaper.Enabled) { _settings.Wallpaper.Enabled = true; WpEnabled.IsChecked = true; }
        _wallpaper.Apply();
        UpdateHero();
        QueueSave();
    }

    void ChooseVideo_Click(object sender, RoutedEventArgs e)
    {
        if (!PickVideo()) return;
        _settings.Wallpaper.Scene = "video";
        foreach (var c in _scenes) c.IsSelected = c.Info.UsesVideo;
        _wallpaper.Apply();
        UpdateHero();
        QueueSave();
    }

    bool PickVideo()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a video wallpaper",
            Filter = "Videos|*.mp4;*.m4v;*.mov;*.mkv;*.wmv;*.webm;*.avi|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return false;
        _settings.Wallpaper.VideoPath = dlg.FileName;
        VideoPathText.Text = dlg.FileName;
        return true;
    }

    // ------------------------------------------------------------------ apps

    void RefreshPins()
    {
        _pins.Clear();
        foreach (var pin in _settings.Pinned)
            _pins.Add(new PinRow(pin, ToBitmap(_dock.Catalog.GetIcon(pin.Path))));
        NoPins.Visibility = _pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in _allApps) a.Refresh();
    }

    void MovePin(PinnedApp pin, int delta)
    {
        int i = _settings.Pinned.IndexOf(pin);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _settings.Pinned.Count) return;
        (_settings.Pinned[i], _settings.Pinned[j]) = (_settings.Pinned[j], _settings.Pinned[i]);
        _settings.Save();
        _dock.Rebuild();
        RefreshPins();
        RefreshMiniDocks();
    }

    void PinUp_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is PinRow r) MovePin(r.Pin, -1); }
    void PinDown_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is PinRow r) MovePin(r.Pin, +1); }

    void PinRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PinRow r) return;
        _settings.Pinned.Remove(r.Pin);
        _settings.Save();
        _dock.Rebuild();
        RefreshPins();
        RefreshMiniDocks();
    }

    void LoadAllApps()
    {
        _allApps = _dock.Catalog.InstalledApps
            .Where(a => !a.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .Select(a => new AppRow(a, _dock.Catalog, _settings))
            .ToList();
        ApplySearch();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    void ApplySearch()
    {
        string q = Search?.Text?.Trim() ?? "";
        AllApps.ItemsSource = string.IsNullOrEmpty(q) ? _allApps : _allApps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    void PinApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AppRow row) return;
        if (row.IsPinned)
        {
            var pin = _settings.Pinned.FirstOrDefault(p => row.Matches(p));
            if (pin != null) { _settings.Pinned.Remove(pin); _settings.Save(); _dock.Rebuild(); }
        }
        else _dock.Pin(row.App);
        RefreshPins();
        RefreshMiniDocks();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Add a program to the dock", Filter = "Programs and shortcuts|*.exe;*.lnk|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        string name = Path.GetFileNameWithoutExtension(path);
        string? args = null;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellInterop.ReadShortcut(path) is { } lnk)
        {
            name = lnk.Name;
            args = lnk.Arguments;
            path = lnk.TargetPath ?? (lnk.AppId != null ? @"shell:AppsFolder\" + lnk.AppId : path);
        }
        _settings.Pinned.Add(new PinnedApp { Path = path, Name = name, Arguments = args });
        _settings.Save();
        _dock.Rebuild();
        RefreshPins();
        RefreshMiniDocks();
    }

    void ImportTaskbar_Click(object sender, RoutedEventArgs e)
    {
        int added = PinImporter.ImportTaskbarPins(_settings);
        _settings.Save();
        _dock.Rebuild();
        RefreshPins();
        RefreshMiniDocks();
        MessageBox.Show(this, added == 0 ? "No new taskbar pins were found." : $"Added {added} app(s) from the Windows taskbar.", "Harbor");
    }

    // ------------------------------------------------------------------ about

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        Startup.SetEnabled(Autostart.IsChecked == true);
        _settings.StartWithWindows = Autostart.IsChecked == true;
        QueueSave();
    }

    void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Directory}\"") { UseShellExecute = true });

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var log = Path.Combine(Log.Directory, "harbor.log");
        if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    void OpenNotices_Click(object sender, RoutedEventArgs e)
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(notices)) Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
    }

    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    static readonly ImageSource HarborIcon = BitmapDecoder
        .Create(new Uri("pack://application:,,,/Assets/Harbor.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
        .Frames.MaxBy(f => f.PixelWidth)!;

    internal static ImageSource? ToBitmap(IconImage? icon)
    {
        if (icon == null) return null;
        var bmp = BitmapSource.Create(icon.Width, icon.Height, 96, 96, PixelFormats.Pbgra32, null, icon.Pixels, icon.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------------ row models

    sealed class PinRow(PinnedApp pin, ImageSource? icon)
    {
        public PinnedApp Pin { get; } = pin;
        public string Name => string.IsNullOrWhiteSpace(Pin.Name) ? Path.GetFileNameWithoutExtension(Pin.Path) : Pin.Name;
        public string Detail => Pin.Path;
        public ImageSource? Icon { get; } = icon;
    }

    sealed class SceneCard(SceneInfo info) : INotifyPropertyChanged
    {
        public SceneInfo Info { get; } = info;
        public string Name => Info.Name;
        public string Description => Info.Description;
        public string CostLabel => Info.Cost switch { SceneCost.Low => "Light", SceneCost.Medium => "Balanced", _ => "Rich" };
        public Visibility VideoGlyph => Info.UsesVideo ? Visibility.Visible : Visibility.Collapsed;

        ImageSource? _preview;
        public ImageSource? Preview { get => _preview; set { _preview = value; Changed(nameof(Preview)); } }

        bool _selected;
        public bool IsSelected { get => _selected; set { _selected = value; Changed(nameof(IsSelected)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    sealed class AppRow : INotifyPropertyChanged
    {
        readonly AppCatalog _catalog;
        readonly AppSettings _settings;
        ImageSource? _icon;
        bool _requested;

        public AppRow(ShellInterop.InstalledApp app, AppCatalog catalog, AppSettings settings)
        {
            App = app;
            _catalog = catalog;
            _settings = settings;
        }

        public ShellInterop.InstalledApp App { get; }
        public string Name => App.Name;
        public bool IsPinned => _settings.Pinned.Any(Matches);
        public string PinLabel => IsPinned ? "Unpin" : "Pin";

        public bool Matches(PinnedApp p)
        {
            if (p.Path.Equals(App.ParsingName, StringComparison.OrdinalIgnoreCase)) return true;
            var exe = AppCatalog.AppIdToPath(App.AppId);
            return exe != null && p.Path.Equals(exe, StringComparison.OrdinalIgnoreCase);
        }

        // Icons load lazily as rows scroll into view (the list is virtualized).
        public ImageSource? Icon
        {
            get
            {
                if (_icon == null && !_requested)
                {
                    _requested = true;
                    var img = _catalog.GetIcon(App.ParsingName);
                    if (img != null) _icon = ToBitmap(img);
                    else _catalog.IconLoaded += OnLoaded;
                }
                return _icon;
            }
        }

        void OnLoaded(string source)
        {
            if (!source.Equals(App.ParsingName, StringComparison.OrdinalIgnoreCase)) return;
            _catalog.IconLoaded -= OnLoaded;
            _icon = ToBitmap(_catalog.GetIcon(source));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinLabel)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
