using System.IO;
using System.Runtime.InteropServices;
using Harbor.Interop;

namespace Harbor.UI;

/// <summary>Notification-area icon via Shell_NotifyIcon, re-added automatically after Explorer restarts.</summary>
internal sealed class TrayIcon : NativeWindow
{
    const int WM_TRAY = Native.WM_APP + 1;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    const int NIN_SELECT = Native.WM_USER, WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);

    readonly int _wmTaskbarCreated;
    readonly IntPtr _icon;
    string _tip = "Harbor";

    public event Action? LeftClick;
    public event Action? RightClick;
    /// <summary>Private WM_APP+2.. messages, e.g. a second Harbor instance asking us to show settings.</summary>
    public event Action<int>? Message;

    public TrayIcon()
    {
        Create("HarborTray", Native.WS_POPUP, Native.WS_EX_TOOLWINDOW, 0, 0, 0, 0, IntPtr.Zero);
        _wmTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Harbor.ico");
        int size = Native.GetSystemMetrics(49 /* SM_CXSMICON */);
        _icon = LoadImage(IntPtr.Zero, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        Add();
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = 4,
    };

    void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref d);
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    public void SetTooltip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    protected override bool WndProc(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg == WM_TRAY)
        {
            int evt = (int)((long)lParam & 0xFFFF);
            if (evt == NIN_SELECT || evt == Native.WM_LBUTTONUP) LeftClick?.Invoke();
            else if (evt == WM_CONTEXTMENU || evt == Native.WM_RBUTTONUP) RightClick?.Invoke();
            return true;
        }
        if (msg >= Native.WM_APP + 2 && msg < Native.WM_APP + 16)
        {
            Message?.Invoke(msg);
            return true;
        }
        if (msg == _wmTaskbarCreated)
        {
            Add();
            return true;
        }
        return false;
    }

    public override void Dispose()
    {
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
        base.Dispose();
    }
}
