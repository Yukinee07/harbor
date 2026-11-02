using System.IO;
using System.Runtime.InteropServices;
using Harbor.Interop;

namespace Harbor.Dock;

/// <summary>
/// Hides the Windows taskbar while Harbor runs. The original auto-hide state is written to disk before
/// anything changes, so a crash or kill is repaired on the next launch.
/// </summary>
internal static class TaskbarController
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;
    const uint EVENT_OBJECT_SHOW = 0x8002;

    static readonly string s_marker = Path.Combine(Settings.AppSettings.Directory, "taskbar-hidden.state");
    static IntPtr s_hook;
    static Native.WinEventProc? s_proc;
    static bool s_hidden;

    public static bool IsHidden => s_hidden;

    /// <summary>Call once at startup: restores the taskbar if a previous run died while it was hidden.</summary>
    public static void RecoverFromCrash()
    {
        if (!File.Exists(s_marker)) return;
        Log.Warn("Found taskbar marker from a previous session; restoring the taskbar");
        RestoreCore(ReadSavedState());
    }

    public static void Hide()
    {
        if (s_hidden) return;
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;

        if (!File.Exists(s_marker))
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
            int state = (int)SHAppBarMessage(ABM_GETSTATE, ref abd);
            Directory.CreateDirectory(Path.GetDirectoryName(s_marker)!);
            File.WriteAllText(s_marker, state.ToString());
        }

        // Auto-hide frees the work area; hiding the windows removes the reveal strip.
        SetAppBarState(tray, ABS_AUTOHIDE);
        HideTrayWindows();
        s_hidden = true;

        // Explorer re-shows the taskbar on Start, Win+T, notifications etc. React to that instantly
        // with an out-of-context event hook instead of polling.
        Native.GetWindowThreadProcessId(tray, out uint pid);
        s_proc ??= OnShow;
        s_hook = Native.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, s_proc, pid, 0, 0);
    }

    static void OnShow(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!s_hidden || idObject != 0 || hwnd == IntPtr.Zero) return;
        var cls = Native.GetWindowClass(hwnd);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") HideWindow(hwnd);
    }

    public static void Restore()
    {
        if (!s_hidden && !File.Exists(s_marker)) return;
        RestoreCore(ReadSavedState());
    }

    /// <summary>After an Explorer restart the taskbar windows are new; hide them again.</summary>
    public static void Reapply()
    {
        if (!s_hidden) return;
        s_hidden = false;
        if (s_hook != IntPtr.Zero) { Native.UnhookWinEvent(s_hook); s_hook = IntPtr.Zero; }
        Hide();
    }

    static void RestoreCore(int savedState)
    {
        if (s_hook != IntPtr.Zero) { Native.UnhookWinEvent(s_hook); s_hook = IntPtr.Zero; }
        s_hidden = false;
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            foreach (var w in TrayWindows())
                Native.SetWindowPos(w, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_SHOWWINDOW | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
            SetAppBarState(tray, savedState);
        }
        try { File.Delete(s_marker); } catch { }
    }

    static int ReadSavedState()
    {
        try { return int.Parse(File.ReadAllText(s_marker).Trim()); }
        catch { return 0; }
    }

    static void SetAppBarState(IntPtr tray, int state)
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray, lParam = (IntPtr)state };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    static IEnumerable<IntPtr> TrayWindows()
    {
        var primary = Native.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) yield return primary;
        IntPtr w = IntPtr.Zero;
        while ((w = Native.FindWindowEx(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return w;
    }

    static void HideTrayWindows()
    {
        foreach (var w in TrayWindows()) HideWindow(w);
    }

    static void HideWindow(IntPtr w) =>
        Native.SetWindowPos(w, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_HIDEWINDOW | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
}
