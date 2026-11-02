using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Harbor.Interop;

namespace Harbor.Dock;

internal sealed class TrackedWindow
{
    public IntPtr Hwnd;
    public uint ProcessId;
    public string Title = "";
    public string? AppId;
    public string? ExePath;
    /// <summary>The package the window's process belongs to, if it has one.</summary>
    public string? Family;
    /// <summary>Grouping key: AppUserModelID when known, otherwise the lowercased executable path.</summary>
    public string GroupKey = "";
    public long LastActiveTick;
    public long FirstSeenTick;
    public bool Flashing;
}

/// <summary>
/// Maintains the set of windows that belong on a taskbar, driven by shell hook and WinEvent notifications
/// instead of polling. Changes are coalesced into a single <see cref="Changed"/> event.
/// </summary>
internal sealed class WindowTracker : NativeWindow
{
    const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWACTIVATED = 4, HSHELL_REDRAW = 6,
        HSHELL_WINDOWREPLACED = 13, HSHELL_RUDEAPPACTIVATED = 0x8004, HSHELL_FLASH = 0x8006, HSHELL_MONITORCHANGED = 16;

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C,
        EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;

    readonly Dictionary<IntPtr, TrackedWindow> _windows = new();
    readonly List<IntPtr> _hooks = new();
    readonly Native.WinEventProc _winEventProc;
    readonly int _wmShellHook;
    readonly uint _ownPid = (uint)Environment.ProcessId;
    long _tick;
    bool _refreshQueued;
    bool _dirty = true; // foreground / attention changed since the last Changed event

    public IReadOnlyCollection<TrackedWindow> Windows => _windows.Values;
    public IntPtr Foreground { get; private set; }

    /// <summary>The window list or foreground changed (coalesced).</summary>
    public event Action? Changed;
    /// <summary>A window asked for attention (FlashWindowEx).</summary>
    public event Action<TrackedWindow>? Flashed;
    /// <summary>Raised for every foreground change, before coalescing; used for fullscreen / auto-hide checks.</summary>
    public event Action<IntPtr>? ForegroundChanged;
    /// <summary>Raised when a window moves/resizes/minimizes in a way that may affect overlap with the dock.</summary>
    public event Action? LayoutMaybeChanged;
    public event Action? ExplorerRestarted;

    readonly int _wmTaskbarCreated;
    readonly System.Windows.Threading.DispatcherTimer _layoutTimer;

    public WindowTracker()
    {
        // Shell hook messages are only delivered to real top-level windows, never message-only ones.
        Create("HarborShellHook", Native.WS_POPUP, Native.WS_EX_TOOLWINDOW, 0, 0, 0, 0, IntPtr.Zero);
        _wmShellHook = Native.RegisterWindowMessage("SHELLHOOK");
        _wmTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        if (!Native.RegisterShellHookWindow(Handle)) Log.Warn("RegisterShellHookWindow failed");

        _winEventProc = OnWinEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        // Deliberately not hooking EVENT_OBJECT_LOCATIONCHANGE: it fires system-wide for every caret and
        // scrollbar move and kept the UI thread busy. Move/size-end plus a slow timer covers layout changes.
        Hook(0x000B, 0x000B); // EVENT_SYSTEM_MOVESIZEEND
        _layoutTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _layoutTimer.Tick += (_, _) => LayoutMaybeChanged?.Invoke();
        _layoutTimer.Start();
        Log.Info($"UI thread id {Native.GetCurrentThreadId()}");

        Foreground = Native.GetForegroundWindow();
        Rescan();
    }

    void Hook(uint min, uint max)
    {
        var h = Native.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0, 0x0002 /* WINEVENT_SKIPOWNPROCESS */);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 /* OBJID_WINDOW */ || idChild != 0 || hwnd == IntPtr.Zero) return;
        switch (evt)
        {
            case EVENT_SYSTEM_FOREGROUND:
                SetForeground(hwnd);
                break;
            case 0x000B: // move/size finished
                LayoutMaybeChanged?.Invoke();
                return;
            case EVENT_OBJECT_NAMECHANGE:
                // Titles change constantly (spinners, clocks, tab names) and don't affect the dock, so only
                // refresh when an untracked window might have just become eligible by gaining a title.
                if (_windows.TryGetValue(hwnd, out var w)) w.Title = Native.GetWindowTitle(hwnd);
                else if (Native.IsWindowVisible(hwnd) && Native.GetAncestor(hwnd, Native.GA_ROOT) == hwnd) QueueRefresh();
                return;
            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                LayoutMaybeChanged?.Invoke();
                break;
        }
        // Only top-level windows can be taskbar windows.
        if (evt != EVENT_OBJECT_DESTROY && Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
        QueueRefresh();
    }

    void SetForeground(IntPtr hwnd)
    {
        Foreground = hwnd;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
        if (_windows.TryGetValue(hwnd, out var w) || (root != IntPtr.Zero && _windows.TryGetValue(root, out w)))
        {
            w.LastActiveTick = ++_tick;
            w.Flashing = false;
        }
        ForegroundChanged?.Invoke(hwnd);
        _dirty = true;
        QueueRefresh();
    }

    void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Native.SetTimer(Handle, (IntPtr)1, 40, IntPtr.Zero);
    }

    protected override bool WndProc(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg == _wmShellHook)
        {
            int code = (int)wParam;
            switch (code)
            {
                case HSHELL_WINDOWACTIVATED:
                case HSHELL_RUDEAPPACTIVATED:
                    if (lParam != IntPtr.Zero) SetForeground(lParam);
                    break;
                case HSHELL_FLASH:
                    if (_windows.TryGetValue(lParam, out var w) && lParam != Native.GetForegroundWindow())
                    {
                        if (!w.Flashing)
                        {
                            w.Flashing = true;
                            _dirty = true;
                            Flashed?.Invoke(w);
                        }
                        QueueRefresh();
                    }
                    break;
                case HSHELL_REDRAW:
                    break; // title/icon redraws are frequent and irrelevant to the dock
                default:
                    QueueRefresh();
                    break;
            }
            return true;
        }
        if (msg == _wmTaskbarCreated)
        {
            ExplorerRestarted?.Invoke();
            Rescan();
            return true;
        }
        if (msg == Native.WM_TIMER && wParam == (IntPtr)1)
        {
            Native.KillTimer(Handle, (IntPtr)1);
            _refreshQueued = false;
            Rescan();
            return true;
        }
        return false;
    }

    /// <summary>Re-enumerates top-level windows. Cheap: per-window process lookups are cached.</summary>
    public void Rescan()
    {
        var seen = new HashSet<IntPtr>();
        bool changed = false;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!IsTaskbarWindow(hwnd)) return true;
            seen.Add(hwnd);
            if (!_windows.ContainsKey(hwnd))
            {
                var w = Describe(hwnd);
                if (w != null)
                {
                    _windows[hwnd] = w;
                    changed = true;
                }
            }
            return true;
        }, IntPtr.Zero);

        foreach (var hwnd in _windows.Keys.Where(h => !seen.Contains(h)).ToList())
        {
            _windows.Remove(hwnd);
            changed = true;
        }
        if (changed || _dirty)
        {
            _dirty = false;
            Changed?.Invoke();
        }
    }

    TrackedWindow? Describe(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid) return null;
        var w = new TrackedWindow
        {
            Hwnd = hwnd,
            ProcessId = pid,
            Title = Native.GetWindowTitle(hwnd),
            FirstSeenTick = ++_tick,
            LastActiveTick = hwnd == Foreground ? ++_tick : 0,
        };
        ResolveIdentity(w);
        if (s_seenGroups.Add(w.GroupKey)) Log.Info($"App seen: {w.GroupKey}, package {w.Family ?? "none"}, exe {Path.GetFileName(w.ExePath) ?? "unknown"}");
        return w;
    }

    static readonly HashSet<string> s_seenGroups = new();

    static void ResolveIdentity(TrackedWindow w)
    {
        w.AppId = ShellInterop.GetWindowAppId(w.Hwnd);
        w.ExePath = Native.GetProcessPath(w.ProcessId);

        if (w.ExePath != null && Path.GetFileName(w.ExePath).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            // UWP frame: the hosted app lives in a child CoreWindow owned by another process.
            IntPtr core = IntPtr.Zero;
            Native.EnumChildWindows(w.Hwnd, (child, _) =>
            {
                Native.GetWindowThreadProcessId(child, out uint cpid);
                if (cpid != w.ProcessId) { core = child; return false; }
                return true;
            }, IntPtr.Zero);
            if (core != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(core, out uint corePid);
                w.ExePath = Native.GetProcessPath(corePid) ?? w.ExePath;
                w.AppId ??= Native.GetProcessAumid(corePid);
            }
        }

        // A window can carry an id of its own, but for a packaged app the process's id is the real one. Otherwise the
        // same app splits into two groups depending on which windows set an id, and shows up twice.
        string? processId = Native.GetProcessAumid(w.ProcessId);
        if (w.AppId == null || (AppIdentity.FamilyOfAppId(processId) != null && AppIdentity.FamilyOfAppId(w.AppId) == null && !AppIdentity.IsWebApp(w.AppId)))
            w.AppId = processId ?? w.AppId;
        w.Family = Native.GetProcessPackageFamily(w.ProcessId);
        if (w.AppId == null && w.ExePath != null && Path.GetFileName(w.ExePath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            w.AppId = "Microsoft.Windows.Explorer";
        w.GroupKey = (w.AppId ?? w.ExePath ?? $"pid:{w.ProcessId}").ToLowerInvariant();
    }

    static readonly HashSet<string> s_ignoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "Windows.Internal.Shell.TabProxyWindow", "XamlExplorerHostIslandWindow", "HarborDock", "HarborShellHook",
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd, string name);

    /// <summary>The taskbar eligibility rules used by Explorer-compatible shells.</summary>
    public static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;
        uint ex = Native.GetExStyle(hwnd);
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if (!appWindow && (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero || (ex & Native.WS_EX_NOACTIVATE) != 0)) return false;
        if (Native.IsCloaked(hwnd)) return false;
        if (GetProp(hwnd, "ITaskList_Deleted") != IntPtr.Zero) return false;
        string cls = Native.GetWindowClass(hwnd);
        if (s_ignoredClasses.Contains(cls)) return false;
        if (Native.GetWindowTextLength(hwnd) == 0 && !appWindow) return false;
        return true;
    }

    public override void Dispose()
    {
        _layoutTimer.Stop();
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
        if (Handle != IntPtr.Zero) Native.DeregisterShellHookWindow(Handle);
        base.Dispose();
    }
}

/// <summary>Activating, minimizing and closing windows that belong to other processes.</summary>
internal static class WindowActions
{
    const int WM_SYSCOMMAND = 0x0112;
    const uint WS_MINIMIZEBOX = 0x00020000;

    public static void Activate(IntPtr hwnd)
    {
        var popup = Native.GetLastActivePopup(hwnd);
        if (popup != IntPtr.Zero && Native.IsWindowVisible(popup)) hwnd = popup;

        if (Native.IsIconic(hwnd))
            Native.SendMessageTimeout(hwnd, WM_SYSCOMMAND, (IntPtr)Native.SC_RESTORE, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 200, out _);

        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;

        // Tapping an unassigned virtual key gives this process "last input" rights, which unlocks
        // SetForegroundWindow without the Alt-key side effects (menu bars flashing).
        TapKey(0xE8);
        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;

        uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint me = Native.GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true))
        {
            try
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally { Native.AttachThreadInput(me, fgThread, false); }
        }
        if (Native.GetForegroundWindow() != hwnd) Native.SwitchToThisWindow(hwnd, true);
    }

    public static void Minimize(IntPtr hwnd)
    {
        if ((Native.GetStyle(hwnd) & WS_MINIMIZEBOX) != 0)
            Native.PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)Native.SC_MINIMIZE, IntPtr.Zero);
        else
            Native.ShowWindowAsync(hwnd, Native.SW_MINIMIZE);
    }

    public static void Close(IntPtr hwnd) => Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static void TapKey(ushort vk)
    {
        var inputs = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = Native.KEYEVENTF_KEYUP } } },
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    public static void KeyCombo(params ushort[] keys)
    {
        var inputs = new List<Native.INPUT>();
        foreach (var k in keys)
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = k } } });
        foreach (var k in keys.Reverse())
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = k, dwFlags = Native.KEYEVENTF_KEYUP } } });
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    public static string? ForegroundProcessName()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        var path = Native.GetProcessPath(pid);
        return path == null ? null : Path.GetFileNameWithoutExtension(path);
    }

    public static bool IsStartMenuOpen()
    {
        var name = ForegroundProcessName();
        return name != null && (name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("SearchApp", StringComparison.OrdinalIgnoreCase));
    }

    public static void OpenStartMenu() => TapKey(Native.VK_LWIN);
}
