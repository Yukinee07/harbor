using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HarborSetup
{
    public partial class SetupWindow : Window
    {
        readonly bool _uninstall, _testUi;
        bool _firstInstall, _busy;

        public SetupWindow(bool uninstall, bool testUi)
        {
            _uninstall = uninstall;
            _testUi = testUi;
            InitializeComponent();
            // An .ico holds several sizes and WPF takes the first, which is too small for a high DPI screen.
            var icons = BitmapDecoder.Create(new Uri("pack://application:,,,/Harbor.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            HeaderIcon.Source = icons.Frames.OrderByDescending(f => f.PixelWidth).First();
            _firstInstall = !Installer.IsInstalled;

            if (uninstall)
            {
                ShowPage(PageUninstall);
            }
            else if (!_firstInstall)
            {
                // Running it again is an update, so don't change what was chosen the first time.
                Autostart.IsChecked = Installer.AutostartEnabled;
                InstallButton.Content = "Update";
                WorkTitle.Text = "Updating Harbor";
            }

            if (testUi)
            {
                ShowActivated = false;
                Topmost = true;
                Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_uninstall) Remove_Click(this, null);
                    else Install_Click(this, null);
                }), DispatcherPriority.ApplicationIdle);
            }
            SourceInitialized += (s, e) =>
            {
                int round = 2; // DWMWCP_ROUND; ignored before Windows 11
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
            };
        }

        void ShowPage(UIElement page)
        {
            foreach (var p in new UIElement[] { PageWelcome, PageWork, PageDone, PageUninstall, PageResult })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        }

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            bool autostart = Autostart.IsChecked == true, desktop = Desktop.IsChecked == true;
            await Work(() => Installer.Install(autostart, desktop, ReportProgress), PageDone, "Harbor couldn't be installed");
        }

        async void Remove_Click(object sender, RoutedEventArgs e)
        {
            bool removeSettings = RemoveSettings.IsChecked == true;
            WorkTitle.Text = "Removing Harbor";
            Bar.IsIndeterminate = false;
            Bar.Value = 0.5;
            await Work(() => Installer.Uninstall(removeSettings), null, "Harbor couldn't be removed");
        }

        void ReportProgress(double fraction) => Dispatcher.BeginInvoke(new Action(() => Bar.Value = fraction));

        async Task Work(Action work, UIElement donePage, string failureTitle)
        {
            _busy = true;
            CloseButton.Visibility = Visibility.Collapsed;
            ShowPage(PageWork);
            try
            {
                await Task.Run(work);
                if (donePage != null)
                {
                    ShowPage(donePage);
                }
                else
                {
                    ResultTitle.Text = "Harbor has been removed";
                    ResultText.Text = "Thanks for trying it.";
                    ShowPage(PageResult);
                }
            }
            catch (Exception ex)
            {
                Installer.Log("Failed: " + ex);
                ResultTitle.Text = failureTitle;
                ResultText.Text = ex.Message + "\n\nDetails are in " + Path.Combine(Path.GetTempPath(), "Harbor-Setup.log");
                ShowPage(PageResult);
            }
            _busy = false;
            CloseButton.Visibility = Visibility.Visible;
            if (_testUi)
            {
                var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                close.Tick += (s, e) => { close.Stop(); Close(); };
                close.Start();
            }
        }

        void Open_Click(object sender, RoutedEventArgs e)
        {
            try { Installer.Launch(_firstInstall); }
            catch (Exception ex) { Installer.Log("Could not open Harbor: " + ex); }
            Close();
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = _busy; // stopping halfway through would leave a partial install
            base.OnClosing(e);
        }

        /// <summary>Developer aid: draws a page to a PNG without ever showing a window.</summary>
        public static void RenderPreview(string page, string file)
        {
            var w = new SetupWindow(page == "uninstall", false);
            switch (page)
            {
                case "update": w.InstallButton.Content = "Update"; break;
                case "working": w.Bar.Value = 0.62; w.ShowPage(w.PageWork); break;
                case "done": w.ShowPage(w.PageDone); break;
                case "removed": w.ResultTitle.Text = "Harbor has been removed"; w.ResultText.Text = "Thanks for trying it."; w.ShowPage(w.PageResult); break;
                case "welcome": w.Autostart.IsChecked = true; w.InstallButton.Content = "Install"; w.ShowPage(w.PageWelcome); break;
            }
            int width = (int)w.Width, height = (int)w.Height;
            w.Root.Measure(new Size(width, height));
            w.Root.Arrange(new Rect(0, 0, width, height));
            w.Root.UpdateLayout();
            const double scale = 1.75;
            w.Root.Background = w.Background;
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(w.Root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
