using System.IO;
using Harbor.Interop;
using Harbor.Settings;
using Microsoft.Win32;

namespace Harbor.UI;

/// <summary>Per-user autostart through HKCU\...\Run (no admin rights needed).</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Harbor";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Seeds the dock from the user's existing Windows taskbar pins.</summary>
internal static class PinImporter
{
    public static int ImportTaskbarPins(AppSettings settings)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        int added = 0;
        if (Directory.Exists(dir))
        {
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk").OrderBy(File.GetCreationTimeUtc))
            {
                var info = ShellInterop.ReadShortcut(lnk);
                if (info == null) continue;
                PinnedApp? pin = null;
                if (info.TargetPath != null && info.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(info.TargetPath))
                    pin = new PinnedApp { Path = info.TargetPath, Name = info.Name, Arguments = info.Arguments, AppId = info.AppId };
                else if (info.AppId != null)
                    pin = new PinnedApp { Path = @"shell:AppsFolder\" + info.AppId, Name = info.Name, AppId = info.AppId };
                if (pin != null && TryAdd(settings, pin)) added++;
            }
        }
        if (settings.Pinned.Count == 0)
        {
            // Sensible defaults when the taskbar had nothing we could read.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (TryAdd(settings, new PinnedApp { Path = explorer, Name = "File Explorer", AppId = "Microsoft.Windows.Explorer" })) added++;
            var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(edge) && TryAdd(settings, new PinnedApp { Path = edge, Name = "Microsoft Edge" })) added++;
            if (TryAdd(settings, new PinnedApp
                {
                    Path = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                    Name = "Settings",
                    AppId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                })) added++;
        }
        return added;
    }

    static bool TryAdd(AppSettings settings, PinnedApp pin)
    {
        if (settings.Pinned.Any(p => p.Path.Equals(pin.Path, StringComparison.OrdinalIgnoreCase)
                                     || (p.AppId != null && pin.AppId != null && p.AppId.Equals(pin.AppId, StringComparison.OrdinalIgnoreCase))))
            return false;
        settings.Pinned.Add(pin);
        return true;
    }
}
