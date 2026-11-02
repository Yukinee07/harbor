using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Harbor.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Harbor.Wallpaper;

internal enum WallpaperState { Stopped, Animated, Paused, Static, Failed }

/// <summary>Everything the render thread needs to know, copied atomically from settings and power policy.</summary>
internal sealed record EngineConfig(
    string Scene,
    string? VideoPath,
    bool Static,
    bool Paused,
    bool Interactive,
    float MouseStrength,
    float Speed,
    int InteractiveFps,
    int AmbientFps,
    float RenderScale,
    float Hue,
    float Saturation,
    float Brightness,
    bool AudioReactive);

[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Vector2 Resolution;
    public float Time, Delta;
    public Vector2 Mouse, MouseVel;
    public float Intensity, Aspect, MouseActive, Interact;
    public Vector4 Audio;
    public Vector4 Grade;
    public Vector4 Params;
    public Vector4 Ripple0, Ripple1, Ripple2, Ripple3;
}

/// <summary>
/// Live wallpaper renderer. Runs on its own thread because its windows become children of Explorer's
/// desktop window, which attaches the two threads' input queues; the dock's UI thread must never be
/// coupled to Explorer that way. One window + swapchain per monitor, paced by a high-resolution
/// waitable timer, and fully idle (zero GPU work, last frame kept on screen) when static or paused.
/// </summary>
internal sealed class WallpaperEngine : IDisposable
{
    const uint INFINITE = 0xFFFFFFFF;
    const uint QS_ALLINPUT = 0x04FF;
    const uint MWMO_INPUTAVAILABLE = 0x0004;

    readonly ConcurrentQueue<Action> _commands = new();
    readonly AutoResetEvent _wake = new(false);
    readonly ConcurrentDictionary<IntPtr, bool> _covered = new();
    Thread? _thread;
    volatile bool _quit;
    EngineConfig _config;

    // Render-thread state.
    ID3D11Device? _device;
    ID3D11DeviceContext? _ctx;
    IDXGIFactory2? _factory;
    ID3D11VertexShader? _vs;
    ID3D11Buffer? _cb;
    ID3D11SamplerState? _sampler, _wrapSampler;
    ID3D11Texture2D? _noise;
    ID3D11ShaderResourceView? _noiseView;
    readonly Dictionary<string, ID3D11PixelShader> _scenes = new();
    readonly List<Surface> _surfaces = new();
    VideoSource? _video;
    string? _videoPath;
    DesktopAttach.Target? _target;
    IntPtr _timer;
    double _time;
    bool _renderOnce, _needsRebuild, _wasDown, _hasVideoDevice, _wasAnimating, _drewFrame;
    float _mouseActive = 1, _interact;
    long _lastInteraction;
    Native.POINT _lastCursor;
    readonly Dictionary<string, ID3D11PixelShader> _simShaders = new();

    /// <summary>Music analysis shared with the controller (null when music reactivity is off).</summary>
    public AudioAnalyzer? Audio { get; set; }
    WallpaperState _state = WallpaperState.Stopped;

    public event Action<WallpaperState>? StateChanged;
    public WallpaperState State => _state;
    public string? LastError { get; private set; }

    /// <summary>Use blt-model presents instead of flip model, for drivers that misbehave on layered children.</summary>
    public static bool UseBltPresent { get; set; } = Environment.GetEnvironmentVariable("HARBOR_BLT_PRESENT") == "1";

    public WallpaperEngine(EngineConfig config) => _config = config;

    public void Start()
    {
        if (_thread != null) return;
        _quit = false;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Harbor wallpaper" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Apply(EngineConfig config) => Post(() =>
    {
        var old = _config;
        _config = config;
        // Moving to a video scene needs a device that can decode; recreating one also rebuilds the video source.
        if (SceneCatalog.Get(config.Scene).UsesVideo && !_hasVideoDevice && _device != null) RecreateDevice();
        else if (old.Scene != config.Scene || old.VideoPath != config.VideoPath) UpdateVideoSource();
        if (old.Scene != config.Scene) foreach (var s in _surfaces) s.ReleaseSim();
        // Entering static or paused: draw one last full quality frame, then go idle.
        _renderOnce = true;
        UpdateVideoPlayback();
        PublishState();
    });

    /// <summary>Monitors fully covered by windows don't need rendering (the desktop isn't visible).</summary>
    public void SetCovered(IntPtr monitor, bool covered)
    {
        if (_covered.TryGetValue(monitor, out bool was) && was == covered) return;
        _covered[monitor] = covered;
        Post(() => { UpdateVideoPlayback(); PublishState(); });
    }

    /// <summary>Re-create the desktop windows, e.g. after Explorer restarted or the monitor layout changed.</summary>
    public void Reattach() => Post(() => _needsRebuild = true);

    /// <summary>Puts the wallpaper back if it's no longer exactly on its screens. Cheap enough to call every couple of seconds.</summary>
    public void CheckPlacement() => Post(VerifyPlacement);

    void Post(Action a)
    {
        _commands.Enqueue(a);
        _wake.Set();
    }

    // ------------------------------------------------------------------ thread

    void ThreadMain()
    {
        try
        {
            Log.Info($"Wallpaper thread id {Native.GetCurrentThreadId()}");
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
            if (_timer == IntPtr.Zero) _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
            CreateDevice();
            Diag.Mark("wallpaper device");
            BuildSurfaces();
            Diag.Mark("wallpaper swapchains");
            UpdateVideoSource();
            _renderOnce = true;
            PublishState();
            Loop();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Wallpaper engine failed", ex);
            SetState(WallpaperState.Failed);
        }
        finally
        {
            Cleanup();
        }
    }

    void Loop()
    {
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle(), _timer };
        long lastFrame = 0;
        while (!_quit)
        {
            while (_commands.TryDequeue(out var cmd))
            {
                try { cmd(); }
                catch (Exception ex) { Log.Error("Wallpaper command failed", ex); }
            }
            if (_quit) break;
            if (_needsRebuild || _surfaces.Any(s => s.Lost))
            {
                _needsRebuild = false;
                BuildSurfaces();
                _renderOnce = true;
            }

            bool animating = IsAnimating();
            if (animating || _renderOnce)
            {
                long now = Stopwatch.GetTimestamp();
                double interval = 1.0 / CurrentFps();
                double since = lastFrame == 0 ? interval : (now - lastFrame) / (double)Stopwatch.Frequency;
                if (since >= interval * 0.92 || _renderOnce)
                {
                    // Static gets one crisp frame. Paused or covered means nobody can see it, so the frame already
                    // there will do, unless nothing has been drawn yet.
                    if (animating || _config.Static || !_drewFrame)
                    {
                        RenderFrame(animating ? Math.Min(since, 0.1) : 0, fullQuality: !animating && _config.Static);
                        _drewFrame = true;
                        lastFrame = now;
                        since = 0;
                    }
                    if (!animating) TrimMemory();
                    _renderOnce = false;
                }
                if (animating) ArmTimer(interval - since);
            }
            else if (_wasAnimating)
            {
                TrimMemory();
            }
            _wasAnimating = animating;

            MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
    }

    /// <summary>
    /// Full rate only while someone is actually interacting with the desktop (or music is driving it);
    /// otherwise the slow ambient rate. Ambient motion is gentle, so it looks the same at half the cost.
    /// </summary>
    double CurrentFps()
    {
        bool interacting = Environment.TickCount64 - _lastInteraction < 1500
                           || (_config.AudioReactive && (Audio?.Level ?? 0) > 0.04f);
        int fps = interacting ? Math.Max(_config.InteractiveFps, _config.AmbientFps) : _config.AmbientFps;
        return Math.Clamp(fps, 5, 240);
    }

    bool IsAnimating()
    {
        if (_config.Static || _config.Paused || _surfaces.Count == 0) return false;
        return _surfaces.Any(s => !IsCovered(s));
    }

    bool IsCovered(Surface s) => _covered.TryGetValue(s.Monitor, out bool c) && c;

    void PublishState()
    {
        var state = _surfaces.Count == 0 ? WallpaperState.Stopped
            : _config.Static ? WallpaperState.Static
            : IsAnimating() ? WallpaperState.Animated
            : WallpaperState.Paused;
        SetState(state);
    }

    void SetState(WallpaperState state)
    {
        if (_state == state) return;
        _state = state;
        Log.Info($"Wallpaper state: {state}");
        StateChanged?.Invoke(state);
    }

    void ArmTimer(double seconds)
    {
        long due = -(long)(Math.Max(seconds, 0.0005) * 10_000_000);
        SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
    }

    // ------------------------------------------------------------------ device

    void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        // The video flag pulls the GPU's whole video decoding stack into the process, so only ask for it when needed.
        _hasVideoDevice = SceneCatalog.Get(_config.Scene).UsesVideo;
        var flags = DeviceCreationFlags.BgraSupport | (_hasVideoDevice ? DeviceCreationFlags.VideoSupport : DeviceCreationFlags.None);
        Gpu.CreateDevice(flags, levels, out _device, out _ctx);
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
        using (var dxgi = _device.QueryInterface<IDXGIDevice1>()) dxgi.MaximumFrameLatency = 1;
        // The swapchain has to come from the factory that owns the adapter the device was made on.
        using (var dxgi = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            _factory = adapter.GetParent<IDXGIFactory2>();

        string common = ReadResource("Common.hlsl");
        string tail = ReadResource("CommonMain.hlsl");
        string stub = common + "\nfloat3 scene(float2 uv, float2 p) { return 0; }\n" + tail;
        _vs = _device.CreateVertexShader(Compile(stub, "VSMain", "vs_5_0", "common"));

        _cb = _device.CreateBuffer((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        _wrapSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        (_noise, _noiseView) = CreateNoiseTexture(_device);
    }

    /// <summary>256x256 single-channel random values, fixed seed so scenes look identical every run.</summary>
    internal static (ID3D11Texture2D, ID3D11ShaderResourceView) CreateNoiseTexture(ID3D11Device device)
    {
        const int size = 256;
        var data = new byte[size * size];
        new Random(1337).NextBytes(data);
        var desc = new Texture2DDescription(Format.R8_UNorm, size, size, 1, 1, BindFlags.ShaderResource,
            ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var tex = device.CreateTexture2D(desc, new SubresourceData(handle.AddrOfPinnedObject(), size));
            return (tex, device.CreateShaderResourceView(tex, null));
        }
        finally { handle.Free(); }
    }

    ID3D11PixelShader SceneShader(string id)
    {
        if (_scenes.TryGetValue(id, out var ps)) return ps;
        var info = SceneCatalog.Get(id);
        var sw = Stopwatch.StartNew();
        string src = ReadResource("Common.hlsl") + "\n" + ReadResource(info.Id + ".hlsl") + "\n" + ReadResource("CommonMain.hlsl");
        ps = _device!.CreatePixelShader(Compile(src, "PSMain", "ps_5_0", info.Id));
        _scenes[id] = ps;
        if (info.UsesSimulation) _simShaders[id] = _device.CreatePixelShader(Compile(src, "PSSim", "ps_5_0", info.Id + "-sim"));
        Log.Info($"Compiled scene {id} in {sw.ElapsedMilliseconds} ms");
        return ps;
    }

    static readonly string s_shaderCache = Path.Combine(Log.Directory, "shaders");

    /// <summary>Compiles HLSL, caching bytecode on disk keyed by a hash of the source.</summary>
    internal static byte[] Compile(string source, string entry, string profile, string name)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(source + "|" + entry + "|" + profile)))[..16];
        string file = Path.Combine(s_shaderCache, $"{name}-{entry}-{key}.cso");
        try { if (File.Exists(file)) return File.ReadAllBytes(file); } catch { }

        var result = Compiler.Compile(source, null!, null!, entry, name, profile, ShaderFlags.OptimizationLevel3, out var blob, out var errors);
        string? err = errors?.AsString();
        errors?.Dispose();
        if (result.Failure || blob == null) throw new InvalidOperationException($"Shader {name}/{entry} failed: {err}");
        var bytes = blob.AsSpan().ToArray();
        blob.Dispose();
        try
        {
            Directory.CreateDirectory(s_shaderCache);
            File.WriteAllBytes(file, bytes);
        }
        catch { }
        return bytes;
    }

    internal static string ReadResource(string file)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("Scenes." + file, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ surfaces

    sealed class Surface : NativeWindow
    {
        public IntPtr Monitor;
        public Native.RECT Screen;
        public IDXGISwapChain1? Swap;
        public ID3D11RenderTargetView? Rtv;
        public int BackW, BackH;
        public Vector2 Mouse = new(0.5f, 0.5f), MouseVel;
        public readonly Vector4[] Ripples = new Vector4[4];
        public bool Lost;
        // Ping-pong state for simulated scenes (e.g. the water pool): R = height now, G = height before.
        public readonly ID3D11Texture2D?[] Sim = new ID3D11Texture2D?[2];
        public readonly ID3D11RenderTargetView?[] SimRtv = new ID3D11RenderTargetView?[2];
        public readonly ID3D11ShaderResourceView?[] SimSrv = new ID3D11ShaderResourceView?[2];
        public int SimW, SimH, SimIndex;

        public void ReleaseSim()
        {
            for (int i = 0; i < 2; i++)
            {
                SimSrv[i]?.Dispose(); SimSrv[i] = null;
                SimRtv[i]?.Dispose(); SimRtv[i] = null;
                Sim[i]?.Dispose(); Sim[i] = null;
            }
        }

        public void CreateHwnd(Native.RECT r) =>
            Create("HarborWallpaper", Native.WS_POPUP, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                r.Left, r.Top, r.Width, r.Height, IntPtr.Zero, "Harbor Wallpaper");

        protected override bool WndProc(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            switch (msg)
            {
                case Native.WM_ERASEBKGND:
                    result = (IntPtr)1;
                    return true;
                case Native.WM_NCHITTEST:
                    result = (IntPtr)Native.HTTRANSPARENT;
                    return true;
                case Native.WM_MOUSEACTIVATE:
                    result = (IntPtr)Native.MA_NOACTIVATE;
                    return true;
                case Native.WM_DESTROY:
                    Lost = true;
                    return false;
            }
            return false;
        }

        public void ReleaseGpu()
        {
            ReleaseSim();
            Rtv?.Dispose(); Rtv = null;
            Swap?.Dispose(); Swap = null;
        }
    }

    void BuildSurfaces()
    {
        foreach (var s in _surfaces) { s.ReleaseGpu(); s.Dispose(); }
        _surfaces.Clear();

        _target = DesktopAttach.Find();
        if (_target == null)
        {
            Log.Warn("No desktop window to attach the wallpaper to");
            PublishState();
            return;
        }
        var origin = DesktopAttach.ParentOrigin(_target);
        Log.Info($"Wallpaper attach: raised={_target.Raised} parent=0x{_target.Parent:X} origin={origin.X},{origin.Y}");

        foreach (var (handle, rect) in Monitors())
        {
            var s = new Surface { Monitor = handle, Screen = rect };
            s.CreateHwnd(rect);
            var local = new Native.RECT(rect.Left - origin.X, rect.Top - origin.Y, rect.Right - origin.X, rect.Bottom - origin.Y);
            DesktopAttach.Attach(s.Handle, _target, local);
            CreateSwapChain(s);
            _surfaces.Add(s);
        }
        PublishState();
    }

    static List<(IntPtr handle, Native.RECT rect)> Monitors()
    {
        var monitors = new List<(IntPtr handle, Native.RECT rect)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
        {
            monitors.Add((h, Native.GetMonitorInfo(h).rcMonitor));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    static bool SameRect(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>
    /// Our windows are children of Explorer's desktop window, so they move when it does. When a screen is added, Explorer
    /// moves and resizes that window a moment after announcing the change, which shifts everything placed in between: with
    /// a screen to the left, the wallpaper ended up one screen too far left and stayed there. So this looks at where things
    /// actually are, rather than trusting any one notification to arrive at the right moment.
    /// </summary>
    void VerifyPlacement()
    {
        if (_target == null || _surfaces.Count == 0 || _needsRebuild) return;

        var monitors = Monitors();
        bool sameScreens = monitors.Count == _surfaces.Count
            && monitors.All(m => _surfaces.Any(s => s.Monitor == m.handle && SameRect(s.Screen, m.rect)));
        if (!sameScreens)
        {
            Log.Info("The screens changed; rebuilding the wallpaper");
            _needsRebuild = true;
            return;
        }

        var origin = DesktopAttach.ParentOrigin(_target);
        foreach (var s in _surfaces)
        {
            if (s.Lost || !Native.GetWindowRect(s.Handle, out var now) || SameRect(now, s.Screen)) continue;
            Log.Info($"Wallpaper window was at {now.Left},{now.Top}; moving it back to {s.Screen.Left},{s.Screen.Top}");
            Native.SetWindowPos(s.Handle, IntPtr.Zero, s.Screen.Left - origin.X, s.Screen.Top - origin.Y, s.Screen.Width, s.Screen.Height,
                Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
    }

    void CreateSwapChain(Surface s)
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)s.Screen.Width,
            Height = (uint)s.Screen.Height,
            Format = Format.B8G8R8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            BufferCount = UseBltPresent ? 1u : 2u,
            SwapEffect = UseBltPresent ? SwapEffect.Discard : SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        s.Swap = _factory!.CreateSwapChainForHwnd(_device!, s.Handle, desc, null, null);
        _factory.MakeWindowAssociation(s.Handle, WindowAssociationFlags.IgnoreAll);
        s.BackW = s.Screen.Width;
        s.BackH = s.Screen.Height;
        using var back = s.Swap.GetBuffer<ID3D11Texture2D>(0);
        s.Rtv = _device!.CreateRenderTargetView(back, null);
    }

    /// <summary>
    /// Below native resolution the swapchain itself shrinks and Windows stretches it to the screen. That's the
    /// same bilinear upscale a shader pass would do, without the extra pass or a second full size texture.
    /// </summary>
    void EnsureBackBuffer(Surface s, int w, int h)
    {
        if (s.Swap == null || (s.BackW == w && s.BackH == h)) return;
        s.Rtv?.Dispose();
        s.Rtv = null;
        _ctx!.ClearState();
        _ctx.Flush();
        s.Swap.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None).CheckError();
        s.BackW = w;
        s.BackH = h;
        using var back = s.Swap.GetBuffer<ID3D11Texture2D>(0);
        s.Rtv = _device!.CreateRenderTargetView(back, null);
    }

    void StepSimulation(Surface s, int steps)
    {
        if (!_simShaders.TryGetValue(_config.Scene, out var sim)) return;
        if (s.Sim[0] == null)
        {
            // A third of the screen is plenty for water: waves are smooth and the render pass filters it.
            float k = Math.Min(1f / 3f, 1100f / s.Screen.Width);
            s.SimW = Math.Max(64, (int)(s.Screen.Width * k));
            s.SimH = Math.Max(64, (int)(s.Screen.Height * k));
            for (int i = 0; i < 2; i++)
            {
                s.Sim[i] = _device!.CreateTexture2D(new Texture2DDescription(Format.R16G16_Float, (uint)s.SimW, (uint)s.SimH, 1, 1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
                s.SimRtv[i] = _device.CreateRenderTargetView(s.Sim[i]!, null);
                s.SimSrv[i] = _device.CreateShaderResourceView(s.Sim[i]!, null);
                _ctx!.ClearRenderTargetView(s.SimRtv[i]!, new Color4(0, 0, 0, 0));
            }
            s.SimIndex = 0;
        }
        _ctx!.RSSetViewport(0, 0, s.SimW, s.SimH, 0, 1);
        _ctx.PSSetShader(sim);
        for (int i = 0; i < steps; i++)
        {
            int src = s.SimIndex, dst = 1 - src;
            _ctx.OMSetRenderTargets(s.SimRtv[dst]!, null);
            _ctx.PSSetShaderResource(2, s.SimSrv[src]!);
            _ctx.Draw(3, 0);
            _ctx.PSUnsetShaderResource(2);
            s.SimIndex = dst;
        }
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null!, null);
        _ctx.PSSetShaderResource(2, s.SimSrv[s.SimIndex]!);
    }

    // ------------------------------------------------------------------ video

    void UpdateVideoSource()
    {
        string? want = SceneCatalog.Get(_config.Scene).UsesVideo ? _config.VideoPath : null;
        if (want != null && !File.Exists(want)) want = null;
        if (want == _videoPath) return;
        _video?.Dispose();
        _video = null;
        _videoPath = want;
        if (want == null || _device == null) return;
        try
        {
            _video = new VideoSource(_device, want);
            UpdateVideoPlayback();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open video {want}", ex);
            LastError = "Could not open the video: " + ex.Message;
        }
    }

    void UpdateVideoPlayback()
    {
        if (_video == null) return;
        if (IsAnimating()) _video.Play();
        else _video.Pause();
    }

    // ------------------------------------------------------------------ rendering

    void SampleInput(float dt)
    {
        Native.GetCursorPos(out var pt);
        bool interactive = _config.Interactive;
        _mouseActive += ((interactive ? 1f : 0f) - _mouseActive) * (1 - MathF.Exp(-dt * 4));

        bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
        bool clicked = down && !_wasDown;
        _wasDown = down;
        // Ripples only for clicks that actually land on the desktop, never on other apps.
        bool desktopClick = clicked && interactive && DesktopAttach.IsDesktopWindow(Native.WindowFromPoint(pt));

        // "Interacting" means the cursor is moving over visible desktop; that's when full frame rate pays off.
        bool moved = Math.Abs(pt.X - _lastCursor.X) + Math.Abs(pt.Y - _lastCursor.Y) > 2;
        _lastCursor = pt;
        long now = Environment.TickCount64;
        if (interactive && (desktopClick || (moved && DesktopAttach.IsDesktopWindow(Native.WindowFromPoint(pt)))))
            _lastInteraction = now;
        float interactTarget = now - _lastInteraction < 1500 ? 1f : 0f;
        _interact += (interactTarget - _interact) * (dt <= 0 ? 1 : 1 - MathF.Exp(-dt * 3));

        float follow = dt <= 0 ? 1 : 1 - MathF.Exp(-dt * 7.5f);
        float velFollow = dt <= 0 ? 1 : 1 - MathF.Exp(-dt * 5f);
        foreach (var s in _surfaces)
        {
            var target = new Vector2((pt.X - s.Screen.Left) / (float)s.Screen.Width, (pt.Y - s.Screen.Top) / (float)s.Screen.Height);
            if (!interactive) target = new Vector2(0.5f, 0.5f);
            var prev = s.Mouse;
            s.Mouse += (target - s.Mouse) * follow;
            if (dt > 0) s.MouseVel += ((s.Mouse - prev) / dt - s.MouseVel) * velFollow;

            for (int i = 0; i < 4; i++)
            {
                ref var r = ref s.Ripples[i];
                if (r.W <= 0) continue;
                r.Z += dt;
                if (r.Z > 4f) r.W = 0;
            }
            if (desktopClick && s.Screen.Contains(pt.X, pt.Y))
            {
                int slot = 0;
                for (int i = 1; i < 4; i++) if (s.Ripples[i].W <= 0 || s.Ripples[i].Z > s.Ripples[slot].Z) slot = i;
                s.Ripples[slot] = new Vector4(target, 0, 1);
            }
        }
    }

    void RenderFrame(double dt, bool fullQuality)
    {
        if (_device == null || _ctx == null) return;
        float fdt = (float)dt;
        _time += dt * Math.Clamp(_config.Speed, 0.05, 4);
        SampleInput(fdt);

        _video?.Update(_device);
        var scene = SceneShader(_config.Scene);
        var info = SceneCatalog.Get(_config.Scene);
        bool video = info.UsesVideo;

        float scale = fullQuality ? 1f : Math.Clamp(_config.RenderScale, 0.25f, 1f);
        foreach (var s in _surfaces)
            if (!s.Lost && (fullQuality || !IsCovered(s)))
                EnsureBackBuffer(s, Math.Max(1, (int)(s.Screen.Width * scale)), Math.Max(1, (int)(s.Screen.Height * scale)));

        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetConstantBuffer(0, _cb);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetSampler(1, _wrapSampler);
        _ctx.PSSetShaderResource(1, _noiseView!);

        foreach (var s in _surfaces)
        {
            if (s.Swap == null || s.Rtv == null || s.Lost) continue;
            if (!fullQuality && IsCovered(s)) continue;

            int w = s.Screen.Width, h = s.Screen.Height, rw = s.BackW, rh = s.BackH;

            var c = new FrameConstants
            {
                Resolution = new Vector2(rw, rh),
                Time = (float)_time,
                Delta = fdt,
                Mouse = s.Mouse,
                MouseVel = s.MouseVel,
                Intensity = _config.Interactive ? Math.Clamp(_config.MouseStrength, 0, 2) : 0,
                Aspect = w / (float)h,
                MouseActive = _mouseActive,
                Interact = _interact,
                Audio = _config.AudioReactive && Audio != null ? new Vector4(Audio.Bass, Audio.Mid, Audio.Treble, Audio.Level) : Vector4.Zero,
                Grade = new Vector4(_config.Hue * MathF.PI / 180f, _config.Saturation, _config.Brightness, 0),
                Params = video && _video != null ? new Vector4(_video.Width, _video.Height, 0, 0) : Vector4.Zero,
                Ripple0 = s.Ripples[0], Ripple1 = s.Ripples[1], Ripple2 = s.Ripples[2], Ripple3 = s.Ripples[3],
            };
            _ctx.UpdateSubresource(in c, _cb!);

            if (video)
            {
                if (_video?.View == null)
                {
                    _ctx.ClearRenderTargetView(s.Rtv, new Color4(0.02f, 0.02f, 0.03f, 1));
                    s.Swap.Present(0, PresentFlags.None);
                    continue;
                }
                _ctx.PSSetShaderResource(0, _video.View);
            }

            if (info.UsesSimulation) StepSimulation(s, fullQuality ? 0 : Math.Clamp((int)Math.Round(dt * 60), 1, 3));

            _ctx.OMSetRenderTargets(s.Rtv, null);
            _ctx.RSSetViewport(0, 0, rw, rh, 0, 1);
            _ctx.PSSetShader(scene);
            _ctx.Draw(3, 0);

            var hr = s.Swap.Present(0, PresentFlags.None);
            if (hr.Code == unchecked((int)0x887A0005) || hr.Code == unchecked((int)0x887A0007))
            {
                Log.Warn("GPU device lost; recreating the wallpaper renderer");
                RecreateDevice();
                return;
            }
        }
    }

    void RecreateDevice()
    {
        foreach (var s in _surfaces) s.ReleaseGpu();
        _video?.Dispose(); _video = null; _videoPath = null;
        DisposeDevice();
        CreateDevice();
        foreach (var s in _surfaces) CreateSwapChain(s);
        UpdateVideoSource();
        _renderOnce = true;
    }

    void TrimMemory()
    {
        // Nothing is being drawn, so only the swapchain's last frame needs to live.
        _ctx?.ClearState();
        _ctx?.Flush();
        using var dxgi = _device?.QueryInterfaceOrNull<IDXGIDevice3>();
        dxgi?.Trim();
    }

    void DisposeDevice()
    {
        foreach (var ps in _scenes.Values) ps.Dispose();
        _scenes.Clear();
        foreach (var ps in _simShaders.Values) ps.Dispose();
        _simShaders.Clear();
        _vs?.Dispose(); _cb?.Dispose(); _sampler?.Dispose(); _wrapSampler?.Dispose();
        _noiseView?.Dispose(); _noise?.Dispose();
        _wrapSampler = null; _noiseView = null; _noise = null;
        _ctx?.ClearState(); _ctx?.Dispose(); _device?.Dispose(); _factory?.Dispose();
        _vs = null; _cb = null; _sampler = null; _ctx = null; _device = null; _factory = null;
    }

    void Cleanup()
    {
        foreach (var s in _surfaces) { s.ReleaseGpu(); s.Dispose(); }
        _surfaces.Clear();
        _video?.Dispose();
        _video = null;
        DisposeDevice();
        if (_target is { Raised: false })
        {
            // Classic desktop: repaint the stock wallpaper over the area we drew on.
            Native.SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, IntPtr.Zero, 0);
        }
        if (_timer != IntPtr.Zero) Native.CloseHandle(_timer);
        _timer = IntPtr.Zero;
        SetState(WallpaperState.Stopped);
    }

    public void Dispose()
    {
        if (_thread == null) return;
        _quit = true;
        _wake.Set();
        _thread.Join(3000);
        _thread = null;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Native.POINT pt; }

    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint ms, uint wakeMask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
}
