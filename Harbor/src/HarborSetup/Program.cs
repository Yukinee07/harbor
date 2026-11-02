using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace HarborSetup
{
    internal static class Program
    {
        /// <summary>
        /// With no arguments: the installer if this exe carries the app, otherwise the uninstaller.
        /// --silent runs with no window (--desktop, --no-autostart, --no-launch, and --remove-settings when uninstalling).
        /// Developer aids: --test-ui clicks through the real window without taking focus; --preview page out.png draws a page to a file.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
            bool uninstall = Has("--uninstall") || !Installer.HasPayload;

            int preview = Array.FindIndex(args, a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
            if (preview >= 0 && args.Length > preview + 2)
            {
                SetupWindow.RenderPreview(args[preview + 1], args[preview + 2]);
                return 0;
            }

            string unsupported = uninstall ? null : Installer.UnsupportedReason();
            if (Has("--silent"))
            {
                if (unsupported != null) { Installer.Log(unsupported); return 2; }
                return RunSilently(uninstall, Has);
            }
            if (unsupported != null)
            {
                MessageBox.Show(unsupported, "Harbor", MessageBoxButton.OK, MessageBoxImage.Information);
                return 2;
            }

            using (new Mutex(true, @"Local\Harbor.Setup", out bool first))
            {
                if (!first) return 3;
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Run(new SetupWindow(uninstall, Has("--test-ui")));
            }
            Installer.FinishOnExit();
            return 0;
        }

        static int RunSilently(bool uninstall, Func<string, bool> has)
        {
            try
            {
                if (uninstall)
                {
                    Installer.Uninstall(has("--remove-settings"));
                    Installer.FinishOnExit();
                    return 0;
                }
                bool firstInstall = !Installer.IsInstalled;
                Installer.Install(!has("--no-autostart"), has("--desktop"), null);
                if (!has("--no-launch")) Installer.Launch(firstInstall);
                return 0;
            }
            catch (Exception ex)
            {
                Installer.Log("Failed: " + ex);
                return 1;
            }
        }
    }
}
