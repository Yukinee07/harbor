using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace HarborSetup
{
    /// <summary>
    /// Everything that changes the PC. Per-user throughout: the app goes under %LOCALAPPDATA%\Programs and every registry
    /// entry under HKCU, so no admin rights are involved and other accounts on the PC are untouched.
    /// </summary>
    internal static class Installer
    {
        public const string Publisher = "Yusuf Yunus";
        const string PayloadName = "Harbor.zip";
        const string UninstallerName = "Uninstall Harbor.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Harbor";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const int WM_APP = 0x8000, WM_HARBOR_QUIT = WM_APP + 3;

        static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        public static readonly string InstallDir = Path.Combine(LocalAppData, "Programs", "Harbor");
        public static readonly string ExePath = Path.Combine(InstallDir, "Harbor.exe");
        static readonly string StartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Harbor.lnk");
        static readonly string DesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Harbor.lnk");
        static readonly string TaskbarMarker = Path.Combine(RoamingAppData, "Harbor", "taskbar-hidden.state");
        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "Harbor-Setup.log");

        static bool _deleteSelfOnExit;

        public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadName) != null;
        public static bool IsInstalled => File.Exists(ExePath);
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool AutostartEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue("Harbor") is string;
            }
        }

        public static string UnsupportedReason()
        {
            if (!Environment.Is64BitOperatingSystem) return "Harbor needs 64-bit Windows.";
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 19041)
                return "Harbor needs Windows 10 version 2004 or newer.";
            return null;
        }

        public static void Log(string message)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine); } catch { }
        }

        // ------------------------------------------------------------------ install

        public static void Install(bool autostart, bool desktopShortcut, Action<double> progress)
        {
            Log("Installing " + Version + " to " + InstallDir);
            StopHarbor();

            // Unpack beside the old copy first, so a failure halfway leaves whatever was working in place.
            string staging = InstallDir + ".new", old = InstallDir + ".old";
            DeleteDirectory(staging);
            DeleteDirectory(old);
            Extract(staging, progress);

            if (Directory.Exists(InstallDir)) Retry(() => Directory.Move(InstallDir, old));
            try
            {
                Retry(() => Directory.Move(staging, InstallDir));
            }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(InstallDir)) Directory.Move(old, InstallDir);
                throw;
            }
            DeleteDirectory(old);

            WriteShortcut(StartMenuShortcut);
            if (desktopShortcut) WriteShortcut(DesktopShortcut);
            WriteUninstallEntry();
            SetAutostart(autostart);
            RestoreTaskbarIfLeftHidden();
            Log("Installed");
        }

        static void Extract(string target, Action<double> progress)
        {
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                foreach (var entry in zip.Entries)
                {
                    string path = Path.Combine(target, entry.FullName.Replace('/', '\\'));
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                    done += entry.Length;
                    progress?.Invoke((double)done / total);
                }
            }
        }

        static void WriteUninstallEntry()
        {
            string uninstaller = Path.Combine(InstallDir, UninstallerName);
            long bytes = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Harbor");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayIcon", ExePath);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --uninstall --silent");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
        }

        static void SetAutostart(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue("Harbor", "\"" + ExePath + "\"");
                else if (RunValuePointsHere(key)) key.DeleteValue("Harbor", false);
            }
        }

        /// <summary>Only ever remove our own entry: a copy of Harbor run from somewhere else keeps its autostart.</summary>
        static bool RunValuePointsHere(RegistryKey key) =>
            key.GetValue("Harbor") is string value && value.IndexOf(InstallDir, StringComparison.OrdinalIgnoreCase) >= 0;

        public static void Launch(bool firstRun)
        {
            Process.Start(new ProcessStartInfo(ExePath, firstRun ? "--settings" : "") { UseShellExecute = true, WorkingDirectory = InstallDir });
        }

        // ------------------------------------------------------------------ uninstall

        public static void Uninstall(bool removeSettings)
        {
            Log("Uninstalling from " + InstallDir);
            StopHarbor();
            RestoreTaskbarIfLeftHidden();

            foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
                if (File.Exists(shortcut)) File.Delete(shortcut);
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (key != null && RunValuePointsHere(key)) key.DeleteValue("Harbor", false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            if (removeSettings)
            {
                DeleteDirectory(Path.Combine(RoamingAppData, "Harbor"));
                DeleteDirectory(Path.Combine(LocalAppData, "Harbor"));
            }

            // Everything except this exe, if it's running from inside the folder. Windows won't delete a running file.
            string self = Assembly.GetExecutingAssembly().Location;
            bool insideInstall = self.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(InstallDir))
            {
                foreach (var file in Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories))
                {
                    if (file.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch (Exception ex) { Log("Could not delete " + file + ": " + ex.Message); }
                }
                foreach (var dir in Directory.EnumerateDirectories(InstallDir))
                    try { Directory.Delete(dir, true); } catch (Exception ex) { Log("Could not delete " + dir + ": " + ex.Message); }
                if (insideInstall) _deleteSelfOnExit = true;
                else DeleteDirectory(InstallDir);
            }
            Log("Uninstalled");
        }

        /// <summary>The last step, once this process is about to end: a hidden cmd waits a moment, then removes the folder.</summary>
        public static void FinishOnExit()
        {
            if (!_deleteSelfOnExit) return;
            string temp = Path.GetTempPath();
            Environment.CurrentDirectory = temp;
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = temp,
            });
        }

        // ------------------------------------------------------------------ the running app

        /// <summary>Asks Harbor to quit, which puts the taskbar and desktop back, and only forces it if it won't.</summary>
        static void StopHarbor()
        {
            var tray = FindWindow("HarborTray", null);
            if (tray != IntPtr.Zero) PostMessage(tray, WM_HARBOR_QUIT, IntPtr.Zero, IntPtr.Zero);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (RunningHarbors().Length > 0 && DateTime.UtcNow < deadline) Thread.Sleep(150);

            foreach (var p in RunningHarbors())
            {
                Log("Harbor didn't quit when asked, so it's being closed");
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception ex) { Log("Could not close Harbor: " + ex.Message); }
            }
        }

        /// <summary>This session's copies of this app, and not some other program that happens to be called Harbor.</summary>
        static Process[] RunningHarbors()
        {
            int session = Process.GetCurrentProcess().SessionId;
            return Process.GetProcessesByName("Harbor").Where(p =>
            {
                try { return p.SessionId == session && FileVersionInfo.GetVersionInfo(p.MainModule.FileName).ProductName == "Harbor"; }
                catch { return false; }
            }).ToArray();
        }

        /// <summary>If Harbor had to be forced closed while hiding the taskbar, it hasn't had the chance to bring it back.</summary>
        static void RestoreTaskbarIfLeftHidden()
        {
            if (!File.Exists(TaskbarMarker) || !File.Exists(ExePath)) return;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(ExePath, "--restore-taskbar") { UseShellExecute = false, WorkingDirectory = InstallDir }))
                    p.WaitForExit(8000);
            }
            catch (Exception ex) { Log("Could not restore the taskbar: " + ex.Message); }
        }

        // ------------------------------------------------------------------ helpers

        static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            try { Retry(() => Directory.Delete(path, true)); }
            catch (Exception ex) { Log("Could not delete " + path + ": " + ex.Message); }
        }

        /// <summary>Antivirus and search indexers hold new files for a moment, so a first failure means little.</summary>
        static void Retry(Action action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { action(); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(250); }
            }
        }

        static void WriteShortcut(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(ExePath);
            link.SetWorkingDirectory(InstallDir);
            link.SetDescription("A floating dock and live wallpaper, by " + Publisher);
            link.SetIconLocation(ExePath, 0);
            ((IPersistFile)link).Save(path, true);
            Marshal.ReleaseComObject(link);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr data, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
    }
}
