using Harbor.Interop;

namespace Harbor.Wallpaper;

/// <summary>
/// Puts a window behind the desktop icons. Windows 11 24H2+ "raised desktop" hosts the icons
/// (SHELLDLL_DefView) and the static wallpaper (WorkerW) as children of Progman, so the wallpaper
/// window is parented to Progman and sandwiched between them. Older builds use the classic WorkerW
/// sibling trick. The layout is detected by probing Progman, not by build number.
/// </summary>
internal static class DesktopAttach
{
    const int SpawnWorkerW = 0x052C;

    public sealed record Target(IntPtr Parent, IntPtr InsertAfter, IntPtr WorkerW, bool Raised);

    public static Target? Find()
    {
        IntPtr progman = Native.GetShellWindow();
        if (progman == IntPtr.Zero) progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return null;

        bool raised = (Native.GetExStyle(progman) & Native.WS_EX_NOREDIRECTIONBITMAP) != 0;
        if (raised)
        {
            IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr workerW = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            // Only ask for a WorkerW when it's missing: sending 0x052C while one exists makes Explorer
            // tear it down and recreate it, which can loop.
            if (workerW == IntPtr.Zero)
            {
                Native.SendMessageTimeout(progman, SpawnWorkerW, (IntPtr)0xD, (IntPtr)1, Native.SMTO_NORMAL, 1000, out _);
                for (int i = 0; i < 20 && workerW == IntPtr.Zero; i++)
                {
                    Thread.Sleep(50);
                    workerW = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                }
            }
            return new Target(progman, defView, workerW, true);
        }

        Native.SendMessageTimeout(progman, SpawnWorkerW, (IntPtr)0xD, (IntPtr)1, Native.SMTO_NORMAL, 1000, out _);
        Native.SendMessageTimeout(progman, SpawnWorkerW, IntPtr.Zero, IntPtr.Zero, Native.SMTO_NORMAL, 1000, out _);
        IntPtr legacyWorker = IntPtr.Zero;
        for (int attempt = 0; attempt < 10 && legacyWorker == IntPtr.Zero; attempt++)
        {
            Native.EnumWindows((top, _) =>
            {
                if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    legacyWorker = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return legacyWorker == IntPtr.Zero;
            }, IntPtr.Zero);
            if (legacyWorker == IntPtr.Zero) Thread.Sleep(50);
        }
        return legacyWorker == IntPtr.Zero ? null : new Target(legacyWorker, IntPtr.Zero, legacyWorker, false);
    }

    /// <summary>Re-parents <paramref name="hwnd"/> under the desktop and places it at the given parent-relative rect.</summary>
    public static void Attach(IntPtr hwnd, Target target, Native.RECT rect)
    {
        // Styles must change before SetParent; some apps lose WS_EX_LAYERED if it's added afterwards.
        uint style = Native.GetStyle(hwnd);
        style = (style & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_CLIPSIBLINGS)) | Native.WS_CHILD;
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)style);
        uint ex = Native.GetExStyle(hwnd) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        if (target.Raised) ex |= Native.WS_EX_LAYERED;
        Native.SetExStyle(hwnd, ex);
        if (target.Raised) Native.SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);

        Native.SetParent(hwnd, target.Parent);
        Native.SetWindowPos(hwnd, target.Raised ? target.InsertAfter : Native.HWND_TOP, rect.Left, rect.Top, rect.Width, rect.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        if (target.Raised) EnsureWorkerWAtBottom(target);
    }

    public static void EnsureWorkerWAtBottom(Target target)
    {
        if (target.WorkerW == IntPtr.Zero || !Native.IsWindow(target.WorkerW)) return;
        IntPtr first = Native.GetWindow(target.Parent, 5 /* GW_CHILD */);
        IntPtr last = first == IntPtr.Zero ? IntPtr.Zero : Native.GetWindow(first, 1 /* GW_HWNDLAST */);
        if (last != target.WorkerW)
            Native.SetWindowPos(target.WorkerW, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Origin of the parent's client area in screen coordinates (the virtual screen on 24H2+).</summary>
    public static Native.POINT ParentOrigin(Target target)
    {
        var pt = new Native.POINT();
        ClientToScreen(target.Parent, ref pt);
        return pt;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT pt);

    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        var cls = Native.GetWindowClass(root);
        return cls is "Progman" or "WorkerW";
    }
}
