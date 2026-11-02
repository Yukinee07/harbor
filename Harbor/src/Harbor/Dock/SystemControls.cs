using System.Runtime.InteropServices;
using Harbor.Interop;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace Harbor.Dock;

/// <summary>The default speakers. Held only while the quick panel is open, so a closed panel costs nothing.</summary>
internal sealed class SpeakerVolume : IDisposable
{
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;

    public bool Available => _device != null;
    public float Level => _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0;
    public bool Muted => _device?.AudioEndpointVolume.Mute ?? false;

    /// <summary>Volume changed outside Harbor. Raised on an audio thread.</summary>
    public event Action? ChangedElsewhere;

    public void Open()
    {
        if (_device != null) return;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnNotification;
        }
        catch (Exception ex)
        {
            Log.Info("No speakers to control: " + ex.Message);
            Dispose();
        }
    }

    void OnNotification(AudioVolumeNotificationData data) => ChangedElsewhere?.Invoke();

    public void Set(float level)
    {
        if (_device == null) return;
        var endpoint = _device.AudioEndpointVolume;
        endpoint.MasterVolumeLevelScalar = Math.Clamp(level, 0, 1);
        if (endpoint.Mute) endpoint.Mute = false; // moving the slider means they want to hear it, as in Windows
    }

    public void ToggleMute()
    {
        if (_device != null) _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
    }

    public void Dispose()
    {
        try
        {
            if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnNotification;
            _device?.Dispose();
            _enumerator?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Closing speakers: " + ex.Message); }
        _device = null;
        _enumerator = null;
    }
}

/// <summary>One screen's brightness, as the quick panel sees it. Reading and setting never wait on the screen.</summary>
internal interface IBrightness
{
    string Name { get; }
    int Minimum { get; }
    int Maximum { get; }
    int Read();
    void Set(int value);
}

/// <summary>
/// The built-in display's backlight, through the video driver's brightness controls. They need no admin rights,
/// take well under a millisecond, and Windows' own slider follows them. See <see cref="ExternalScreens"/> for the rest.
/// </summary>
internal sealed class ScreenBrightness : IDisposable, IBrightness
{
    public string Name => "Laptop screen";

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint IOCTL_VIDEO_QUERY_SUPPORTED_BRIGHTNESS = 0x230494, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS = 0x230498, IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS = 0x23049C;
    const byte DISPLAYPOLICY_AC = 1, DISPLAYPOLICY_BOTH = 3;

    SafeFileHandle? _lcd;
    byte[] _levels = Array.Empty<byte>();
    int _lastRead;

    public bool Available => _lcd != null;
    public int Minimum => _levels.Length > 0 ? _levels[0] : 0;
    public int Maximum => _levels.Length > 0 ? _levels[^1] : 100;

    public void Open()
    {
        if (_lcd != null) return;
        var lcd = CreateFile(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        var levels = new byte[256];
        if (lcd.IsInvalid || !DeviceIoControl(lcd, IOCTL_VIDEO_QUERY_SUPPORTED_BRIGHTNESS, null, 0, levels, levels.Length, out int count, IntPtr.Zero) || count == 0)
        {
            lcd.Dispose();
            return;
        }
        _levels = levels[..count];
        Array.Sort(_levels);
        _lcd = lcd;
    }

    public int Read()
    {
        if (_lcd == null) return _lastRead;
        var state = new byte[3];
        // A failed read keeps the last value, so the slider doesn't drop to zero over a passing driver error.
        if (DeviceIoControl(_lcd, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS, null, 0, state, state.Length, out _, IntPtr.Zero))
            _lastRead = state[0] == DISPLAYPOLICY_AC ? state[1] : state[2];
        return _lastRead;
    }

    public void Set(int percent)
    {
        if (_lcd == null) return;
        byte level = Nearest(percent);
        var state = new[] { DISPLAYPOLICY_BOTH, level, level };
        if (!DeviceIoControl(_lcd, IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS, state, state.Length, null, 0, out _, IntPtr.Zero))
            Log.Warn($"Setting brightness failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Some panels only offer a handful of steps.</summary>
    byte Nearest(int percent)
    {
        byte best = _levels[0];
        foreach (byte l in _levels)
            if (Math.Abs(l - percent) < Math.Abs(best - percent)) best = l;
        return best;
    }

    public void Dispose()
    {
        _lcd?.Dispose();
        _lcd = null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}

/// <summary>
/// External monitors, through DDC/CI: commands sent to the monitor itself down the display cable. Each one takes tens of
/// milliseconds and monitors can choke on a rapid stream, so everything runs one at a time on a background task, and a
/// slider being dragged only ever sends the latest value. Monitors that don't answer simply don't get a slider.
/// </summary>
internal sealed class ExternalScreens
{
    const byte VcpBrightness = 0x10;
    const int GapBetweenCommandsMs = 50;
    static readonly bool s_disabled = Environment.GetEnvironmentVariable("HARBOR_NO_EXTERNAL") == "1"; // developer aid

    sealed class Screen : IBrightness
    {
        public required ExternalScreens Owner;
        public required string Key;
        public required string Name { get; init; }
        public IntPtr Handle;
        public uint MaxRaw;
        public volatile int Value;
        int _writeQueued;

        public int Minimum => 0;
        public int Maximum => 100;
        public int Read() => Value;

        public void Set(int value)
        {
            Value = Math.Clamp(value, 0, 100);
            if (Interlocked.Exchange(ref _writeQueued, 1) == 1) return; // the write already waiting will pick this value up
            Owner.Queue(() =>
            {
                Interlocked.Exchange(ref _writeQueued, 0);
                if (Handle == IntPtr.Zero) return;
                if (!SetVCPFeature(Handle, VcpBrightness, (uint)Math.Round(Value * MaxRaw / 100.0)))
                    Log.Warn($"{Name} refused a brightness change: {Marshal.GetLastWin32Error()}");
                Thread.Sleep(GapBetweenCommandsMs);
            });
        }
    }

    /// <summary>Every dock's panel uses this one, because two sets of commands on one display cable would collide.</summary>
    public static ExternalScreens Shared { get; } = new();

    readonly System.Windows.Threading.Dispatcher _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    readonly object _gate = new();
    Task _tail = Task.CompletedTask;
    volatile List<Screen> _screens = new();
    volatile bool _holding;
    int _holders, _scanQueued;

    /// <summary>The scan finished. Raised on the thread that created this object.</summary>
    public event Action? Changed;

    public IReadOnlyList<IBrightness> Screens => _screens;

    void Queue(Action work)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { Log.Warn("External screen: " + ex.Message); }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>The panel is opening: find the monitors, read their brightness and hold on to them, all in the background.</summary>
    public void Open()
    {
        _holding = ++_holders > 0;
        Probe();
    }

    /// <summary>Finds out which monitors there are and what they're set to, without holding on to them afterwards.</summary>
    public void Probe()
    {
        if (s_disabled || Interlocked.Exchange(ref _scanQueued, 1) == 1) return;
        Queue(() =>
        {
            Interlocked.Exchange(ref _scanQueued, 0);
            Scan();
            if (!_holding) CloseHandles();
            _ui.BeginInvoke(() => Changed?.Invoke());
        });
    }

    /// <summary>Lets go of the monitors while the panel is closed. What they last reported is kept for next time.</summary>
    public void Release()
    {
        _holding = --_holders > 0;
        Queue(() => { if (!_holding) CloseHandles(); });
    }

    void CloseHandles()
    {
        foreach (var screen in _screens) CloseHandle(screen);
    }

    static void CloseHandle(Screen screen)
    {
        if (screen.Handle != IntPtr.Zero) DestroyPhysicalMonitor(screen.Handle);
        screen.Handle = IntPtr.Zero;
    }

    void Scan()
    {
        var known = _screens;
        var found = new List<Screen>();
        var names = DisplayNames.Read();
        var monitors = new List<IntPtr>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
        {
            monitors.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (var monitor in monitors)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info)) continue;
            names.TryGetValue(info.szDevice, out var display);
            if (display.Internal) continue; // the built-in slider already covers it, more reliably
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) continue;
            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) continue;

            for (int i = 0; i < physical.Length; i++)
            {
                IntPtr handle = physical[i].hPhysicalMonitor;
                if (!ReadBrightness(handle, out uint current, out uint max))
                {
                    DestroyPhysicalMonitor(handle);
                    continue;
                }
                string key = info.szDevice + "#" + i;
                var screen = known.FirstOrDefault(s => s.Key == key);
                if (screen == null)
                {
                    string name = !string.IsNullOrWhiteSpace(display.Name) ? display.Name : "Screen " + (found.Count + 2);
                    screen = new Screen { Owner = this, Key = key, Name = name.Length > 28 ? name[..28] : name };
                }
                CloseHandle(screen);
                screen.Handle = handle;
                screen.MaxRaw = max;
                screen.Value = (int)Math.Round(current * 100.0 / max);
                found.Add(screen);
            }
        }

        foreach (var gone in known.Where(s => !found.Contains(s))) CloseHandle(gone);
        _screens = found;
    }

    /// <summary>Monitors miss the odd command, so a single silence doesn't mean they can't do it.</summary>
    static bool ReadBrightness(IntPtr handle, out uint current, out uint max)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (GetVCPFeatureAndVCPFeatureReply(handle, VcpBrightness, out _, out current, out max) && max > 0) return true;
            if (attempt == 1) return false;
            Thread.Sleep(GapBetweenCommandsMs);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr monitor);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}

/// <summary>The names monitors give themselves ("C27F591"), and which screens are built in, from Windows' display configuration.</summary>
internal static class DisplayNames
{
    public readonly record struct Display(string Name, bool Internal);

    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;
    const int TECH_LVDS = 6, TECH_DISPLAYPORT_EMBEDDED = 11, TECH_UDI_EMBEDDED = 13, TECH_INTERNAL = unchecked((int)0x80000000);

    public static Dictionary<string, Display> Read()
    {
        var result = new Dictionary<string, Display>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return result;
        var paths = new PATH_INFO[pathCount];
        var modes = new byte[modeCount * 64];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

        for (int i = 0; i < pathCount; i++)
        {
            var source = new SOURCE_NAME { header = Header(GET_SOURCE_NAME, Marshal.SizeOf<SOURCE_NAME>(), paths[i].sourceAdapter, paths[i].sourceId) };
            if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;
            var target = new TARGET_NAME { header = Header(GET_TARGET_NAME, Marshal.SizeOf<TARGET_NAME>(), paths[i].targetAdapter, paths[i].targetId) };
            string name = DisplayConfigGetDeviceInfo(ref target) == 0 ? target.monitorFriendlyDeviceName : "";
            int tech = paths[i].outputTechnology;
            result[source.viewGdiDeviceName] = new Display(name, tech is TECH_LVDS or TECH_DISPLAYPORT_EMBEDDED or TECH_UDI_EMBEDDED or TECH_INTERNAL);
        }
        return result;
    }

    static HEADER Header(int type, int size, LUID adapter, uint id) => new() { type = type, size = size, adapterId = adapter, id = id };

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint low; public int high; }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_INFO
    {
        public LUID sourceAdapter;
        public uint sourceId, sourceModeIndex, sourceStatus;
        public LUID targetAdapter;
        public uint targetId, targetModeIndex;
        public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator;
        public int scanLineOrdering, targetAvailable;
        public uint targetStatus, flags;
    }

    [StructLayout(LayoutKind.Sequential)] struct HEADER { public int type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SOURCE_NAME
    {
        public HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_NAME
    {
        public HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufacturer, edidProduct;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME request);
}

internal readonly record struct BatteryState(bool Present, int Percent, bool Plugged, bool Charging, bool Saver)
{
    public static BatteryState Read()
    {
        if (!Native.GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100)
            return default;
        return new BatteryState(true, s.BatteryLifePercent, s.ACLineStatus == 1, (s.BatteryFlag & 8) != 0, s.SystemStatusFlag == 1);
    }
}
