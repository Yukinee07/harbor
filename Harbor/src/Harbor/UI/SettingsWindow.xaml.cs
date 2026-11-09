using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            CenterOnPointerScreen();
        };
        Loaded += (_, _) => { CenterOnPointerScreen(); FitContent(); };
        // Not SizeChanged: the viewport figures are only up to date once the scroll viewer has reported this.
        Scroller.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitContent(); };
        PinnedList.SizeChanged += (_, _) => FitContent();
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
        AboutVersion.Text = $"Version {version?.ToString(3)}";
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

    /// <summary>
    /// In real pixels, because WPF's own centring goes wrong across screens scaled differently and could put the top of the
    /// window off the screen. Done twice: landing on a differently scaled screen resizes the window.
    /// </summary>
    void CenterOnPointerScreen()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetCursorPos(out var pt) || !Native.GetWindowRect(hwnd, out var r)) return;
        var work = Native.GetMonitorInfo(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST)).rcWork;
        int x = work.Left + Math.Max(0, (work.Width - r.Width) / 2), y = work.Top + Math.Max(0, (work.Height - r.Height) / 2);
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public static readonly DependencyProperty GalleryColumnsProperty = DependencyProperty.Register(nameof(GalleryColumns), typeof(int), typeof(SettingsWindow), new PropertyMetadata(4));
    public static readonly DependencyProperty ThumbHeightProperty = DependencyProperty.Register(nameof(ThumbHeight), typeof(double), typeof(SettingsWindow), new PropertyMetadata(98.0));
    public int GalleryColumns { get => (int)GetValue(GalleryColumnsProperty); set => SetValue(GalleryColumnsProperty, value); }
    public double ThumbHeight { get => (double)GetValue(ThumbHeightProperty); set => SetValue(ThumbHeightProperty, value); }

    /// <summary>Lets the pages use the room there is, so a bigger or maximized window shows bigger pictures and more of them.</summary>
    void FitContent()
    {
        double width = Math.Min(Scroller.ViewportWidth - Pages.Margin.Left - Pages.Margin.Right, Pages.MaxWidth);
        if (width <= 0) return;
        GalleryColumns = Math.Clamp((int)(width / 190), 4, 8);
        ThumbHeight = Math.Round(((width + 12) / GalleryColumns - 12) * 0.56);
        Hero.Height = Math.Clamp(width * 0.39, 260, Math.Max(300, Scroller.ViewportHeight * 0.55));
        DockStage.Height = Math.Clamp(width * 0.2, 160, 260);
        OptionsGrid.Columns = width >= 1080 ? 3 : 2;
        AllApps.Height = Math.Max(320, Scroller.ViewportHeight - PinnedPanel.ActualHeight - 110);
        UpdateLivePreview();
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
            "general" or "power" or "about" => NavGeneral,
            _ => NavWallpaper,
        }).IsChecked = true;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageWallpaper == null) return;
        var pages = new (RadioButton nav, FrameworkElement page)[]
        {
            (NavWallpaper, PageWallpaper), (NavDock, PageDock), (NavApps, PageApps), (NavGeneral, PageGeneral),
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
        if (PageWallpaper == null) return;
        // The preview follows the same power rules as the wallpaper: on battery outside Full, show a still.
        bool powerOk = !_wallpaper.OnBattery || _wallpaper.Tier == WallpaperTier.Full;
        bool want = IsVisible && WindowState != WindowState.Minimized && PageWallpaper.Visibility == Visibility.Visible && powerOk;
        var scene = SceneCatalog.Get(_settings.Wallpaper.Scene);
        if (want && !scene.UsesVideo)
        {
            // A larger picture when the window is big enough to show one.
            int liveWidth = Hero.ActualWidth > 1000 ? 960 : 640;
            if (_live != null && _live.Width != liveWidth)
            {
                _live.Dispose();
                _live = null;
            }
            _live ??= new LivePreview(Dispatcher, scene.Id, liveWidth, liveWidth * 9 / 16);
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
        VideoRow.Visibility = scene.UsesVideo ? Visibility.Visible : Visibility.Collapsed;
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
        MagnifyAmount.Value = d.Magnification ? d.MagnificationAmount : 0;
        ThemeSystem.IsChecked = d.Theme == HThemeMode.System;
        ThemeDark.IsChecked = d.Theme == HThemeMode.Dark;
        ThemeLight.IsChecked = d.Theme == HThemeMode.Light;
        HideNever.IsChecked = d.AutoHide == AutoHideMode.Never;
        HideOverlap.IsChecked = d.AutoHide == AutoHideMode.WhenOverlapped;
        HideAlways.IsChecked = d.AutoHide == AutoHideMode.Always;
        HideTaskbar.IsChecked = d.HideWindowsTaskbar;
        Previews.IsChecked = d.WindowPreviews;
        ShowHarbor.IsChecked = d.ShowHarborButton;
        ShowClockToggle.IsChecked = d.ShowClock;
        ShowPomodoroToggle.IsChecked = d.ShowPomodoro;
        var pomodoro = _settings.Pomodoro;
        Focus25.IsChecked = pomodoro.FocusMinutes <= 25;
        Focus45.IsChecked = pomodoro.FocusMinutes is > 25 and <= 45;
        Focus60.IsChecked = pomodoro.FocusMinutes > 45;
        Break5.IsChecked = pomodoro.BreakMinutes <= 5;
        Break10.IsChecked = pomodoro.BreakMinutes is > 5 and <= 10;
        Break15.IsChecked = pomodoro.BreakMinutes > 10;
        ShowAllScreens.IsChecked = d.ShowOnAllScreens;
        ShowStart.IsChecked = d.ShowStartButton;
        ShowMedia.IsChecked = d.ShowNowPlaying;
        ShowBin.IsChecked = d.ShowRecycleBin;

        var w = _settings.Wallpaper;
        WpEnabled.IsChecked = w.Enabled;
        HomeSmart.IsChecked = w.Mode == WallpaperMode.Smart;
        HomeAnimated.IsChecked = w.Mode == WallpaperMode.Animated;
        HomeStatic.IsChecked = w.Mode == WallpaperMode.Static;
        WpInteractive.IsChecked = w.Interactive;
        BatEco.IsChecked = w.OnBattery == BatteryBehavior.Eco;
        BatStatic.IsChecked = w.OnBattery == BatteryBehavior.Static;
        BatFull.IsChecked = w.OnBattery == BatteryBehavior.Full;
        QualityLight.IsChecked = w.RenderScale < 0.6;
        QualityBalanced.IsChecked = w.RenderScale is >= 0.6 and < 0.99;
        QualityBest.IsChecked = w.RenderScale >= 0.99;
        if (!string.IsNullOrEmpty(w.VideoPath)) VideoPathText.Text = Path.GetFileName(w.VideoPath);

        Autostart.IsChecked = Startup.IsEnabled;
        _loading = false;
    }

    void Dock_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var d = _settings.Dock;
        d.IconSize = Math.Round(IconSize.Value);
        // One slider does both jobs: all the way down is off.
        d.Magnification = MagnifyAmount.Value >= 0.05;
        if (d.Magnification) d.MagnificationAmount = Math.Round(MagnifyAmount.Value, 2);
        d.Theme = ThemeDark.IsChecked == true ? HThemeMode.Dark : ThemeLight.IsChecked == true ? HThemeMode.Light : HThemeMode.System;
        d.AutoHide = HideAlways.IsChecked == true ? AutoHideMode.Always : HideOverlap.IsChecked == true ? AutoHideMode.WhenOverlapped : AutoHideMode.Never;
        d.HideWindowsTaskbar = HideTaskbar.IsChecked == true;
        d.WindowPreviews = Previews.IsChecked == true;
        d.ShowHarborButton = ShowHarbor.IsChecked == true;
        d.ShowClock = ShowClockToggle.IsChecked == true;
        d.ShowPomodoro = ShowPomodoroToggle.IsChecked == true;
        _settings.Pomodoro.FocusMinutes = Focus60.IsChecked == true ? 60 : Focus45.IsChecked == true ? 45 : 25;
        _settings.Pomodoro.BreakMinutes = Break15.IsChecked == true ? 15 : Break10.IsChecked == true ? 10 : 5;
        d.ShowOnAllScreens = ShowAllScreens.IsChecked == true;
        d.ShowStartButton = ShowStart.IsChecked == true;
        d.ShowNowPlaying = ShowMedia.IsChecked == true;
        d.ShowRecycleBin = ShowBin.IsChecked == true;
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
        w.Mode = HomeStatic.IsChecked == true ? WallpaperMode.Static : HomeAnimated.IsChecked == true ? WallpaperMode.Animated : WallpaperMode.Smart;
        w.Interactive = WpInteractive.IsChecked == true;
        w.OnBattery = BatStatic.IsChecked == true ? BatteryBehavior.Static : BatFull.IsChecked == true ? BatteryBehavior.Full : BatteryBehavior.Eco;
        _wallpaper.Apply();
        QueueSave();
    }

    void Quality_Click(object sender, RoutedEventArgs e)
    {
        var w = _settings.Wallpaper;
        (w.RenderScale, w.AmbientFrameRate, w.FrameRateLimit) =
            QualityLight.IsChecked == true ? (0.5, 24, 30) : QualityBest.IsChecked == true ? (1.0, 30, 60) : (0.75, 30, 60);
        _wallpaper.Apply();
        QueueSave();
    }

    void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void UpdateStatus()
    {
        HomeNowDetail.Text = _wallpaper.StatusDetail;
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
        VideoPathText.Text = Path.GetFileName(dlg.FileName);
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

    // ------------------------------------------------------------------ about

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        Startup.SetEnabled(Autostart.IsChecked == true);
        _settings.StartWithWindows = Autostart.IsChecked == true;
        QueueSave();
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
