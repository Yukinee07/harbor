using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Harbor.Interop;

/// <summary>
/// Minimal raw Win32 window. Used where WPF's own HWNDs get in the way (composition-only surfaces,
/// message-only windows, wallpaper host). Messages are pumped by the WPF dispatcher loop.
/// </summary>
internal class NativeWindow : IDisposable
{
    static readonly Native.WndProc s_proc = StaticWndProc;
    static readonly ConcurrentDictionary<IntPtr, NativeWindow> s_windows = new();
    static readonly HashSet<string> s_registered = new();
    [ThreadStatic] static NativeWindow? t_creating;

    public IntPtr Handle { get; private set; }

    protected void Create(string className, uint style, uint exStyle, int x, int y, int w, int h, IntPtr parent, string? title = null)
    {
        bool register;
        lock (s_registered) register = s_registered.Add(className);
        if (register)
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = Native.GetModuleHandle(null),
                hCursor = Native.LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */),
                lpszClassName = className,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx " + className);
        }

        t_creating = this;
        try
        {
            Handle = Native.CreateWindowEx(exStyle, className, title ?? className, style, x, y, w, h, parent, IntPtr.Zero,
                Native.GetModuleHandle(null), IntPtr.Zero);
        }
        finally { t_creating = null; }

        if (Handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx " + className);
        s_windows[Handle] = this;
    }

    static IntPtr StaticWndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l)
    {
        if (!s_windows.TryGetValue(hwnd, out var window))
        {
            // Messages sent during CreateWindowEx arrive before Handle is assigned.
            window = t_creating;
            if (window != null)
            {
                window.Handle = hwnd;
                s_windows[hwnd] = window;
            }
        }

        // These windows live as long as the app. Left to DefWindowProc, a close message from any outside tool
        // would destroy the dock while Harbor kept running with nothing on screen. Dispose destroys them directly.
        if (msg == Native.WM_CLOSE)
        {
            Log.Info($"Ignored a close message sent to {window?.GetType().Name}");
            return IntPtr.Zero;
        }

        if (window != null)
        {
            try
            {
                if (window.WndProc(msg, w, l, out IntPtr result)) return result;
            }
            catch (Exception ex)
            {
                Log.Error($"WndProc {window.GetType().Name} msg 0x{msg:X}", ex);
            }
            if (msg == Native.WM_DESTROY)
            {
                s_windows.TryRemove(hwnd, out _);
                window.Handle = IntPtr.Zero;
            }
        }
        return Native.DefWindowProc(hwnd, msg, w, l);
    }

    /// <summary>Return true when the message was handled; <paramref name="result"/> is then returned to Windows.</summary>
    protected virtual bool WndProc(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    public virtual void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            var h = Handle;
            Native.DestroyWindow(h);
            s_windows.TryRemove(h, out _);
            Handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
