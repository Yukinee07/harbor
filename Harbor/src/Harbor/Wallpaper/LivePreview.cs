using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Harbor.Wallpaper;

/// <summary>
/// A small live rendering of a scene for the settings window: same shaders as the wallpaper, drawn
/// offscreen at a modest size and frame rate, and steered by the cursor when it is over the preview.
/// Runs only while it is visible.
/// </summary>
internal sealed class LivePreview : IDisposable
{
    readonly Dispatcher _ui;
    readonly int _width, _height;
    readonly object _gate = new();
    Thread? _thread;
    volatile bool _run;
    string _scene;
    Vector2 _mouse = new(0.6f, 0.45f), _target = new(0.6f, 0.45f);
    bool _hasPointer;
    int _pendingWrite;
    float _clickAge = 99;
    Vector2 _click;

    public WriteableBitmap Bitmap { get; }
    public int Width => _width;

    public LivePreview(Dispatcher ui, string scene, int width = 960, int height = 540)
    {
        _ui = ui;
        _scene = scene;
        _width = width;
        _height = height;
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
    }

    public string Scene
    {
        get => _scene;
        set { lock (_gate) _scene = value; }
    }

    /// <summary>Cursor position over the preview in 0..1, or null when it isn't over it.</summary>
    public void SetPointer(Vector2? uv)
    {
        lock (_gate)
        {
            _hasPointer = uv.HasValue;
            if (uv.HasValue) _target = uv.Value;
        }
    }

    public void Click(Vector2 uv)
    {
        lock (_gate) { _click = uv; _clickAge = 0; }
    }

    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Harbor live preview" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(1000);
        _thread = null;
    }

    void Run()
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? ctx = null;
        try
        {
            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
            Harbor.Interop.Gpu.CreateDevice(DeviceCreationFlags.BgraSupport, levels, out device, out ctx);
            string common = WallpaperEngine.ReadResource("Common.hlsl"), tail = WallpaperEngine.ReadResource("CommonMain.hlsl");
            using var vs = device!.CreateVertexShader(WallpaperEngine.Compile(common + "\nfloat3 scene(float2 uv, float2 p) { return 0; }\n" + tail, "VSMain", "vs_5_0", "common"));
            using var cb = device.CreateBuffer((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            var (noise, noiseView) = WallpaperEngine.CreateNoiseTexture(device);
            using var _n = noise;
            using var _nv = noiseView;
            using var clamp = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
            using var wrap = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
            using var target = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_width, (uint)_height, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            using var rtv = device.CreateRenderTargetView(target, null);
            using var staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_width, (uint)_height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));

            string? loaded = null;
            BakedLayers? baked = null;
            ID3D11PixelShader? ps = null, sim = null;
            ID3D11Texture2D[]? simTex = null;
            ID3D11RenderTargetView[]? simRtv = null;
            ID3D11ShaderResourceView[]? simSrv = null;
            int simIndex = 0, sw = _width / 3, sh = _height / 3;
            byte[] bufA = new byte[_width * _height * 4], bufB = new byte[_width * _height * 4];
            var pixels = bufA;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double last = 0, time = 20;

            while (_run)
            {
                string scene;
                Vector2 targetUv;
                bool pointer;
                lock (_gate) { scene = _scene; targetUv = _target; pointer = _hasPointer; }
                var info = SceneCatalog.Get(scene);
                if (info.UsesVideo) { Thread.Sleep(200); continue; }
                if (loaded != scene)
                {
                    ps?.Dispose(); sim?.Dispose(); ps = null; sim = null;
                    string src = common + "\n" + WallpaperEngine.ReadResource(info.Id + ".hlsl") + "\n" + tail;
                    ps = device.CreatePixelShader(WallpaperEngine.Compile(src, "PSMain", "ps_5_0", info.Id));
                    baked?.Dispose();
                    baked = info.UsesBake ? SakuraPainter.Bake(device, _width) : null;
                    if (info.UsesSimulation)
                    {
                        sim = device.CreatePixelShader(WallpaperEngine.Compile(src, "PSSim", "ps_5_0", info.Id + "-sim"));
                        if (simTex == null)
                        {
                            var sd = new Texture2DDescription(Format.R16G16_Float, (uint)sw, (uint)sh, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
                            simTex = new[] { device.CreateTexture2D(sd), device.CreateTexture2D(sd) };
                            simRtv = simTex.Select(t => device.CreateRenderTargetView(t, null)).ToArray();
                            simSrv = simTex.Select(t => device.CreateShaderResourceView(t, null)).ToArray();
                        }
                        foreach (var r in simRtv!) ctx!.ClearRenderTargetView(r, new Color4(0, 0, 0, 0));
                    }
                    loaded = scene;
                }

                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, 0.1);
                last = now;
                time += dt;
                // When the user isn't pointing at it, a slow figure-eight shows off the interaction.
                if (!pointer) targetUv = new Vector2(0.5f + 0.28f * MathF.Sin((float)time * 0.35f), 0.5f + 0.18f * MathF.Sin((float)time * 0.7f));
                var prev = _mouse;
                _mouse += (targetUv - _mouse) * (1 - MathF.Exp(-dt * 6));
                Vector4 ripple = Vector4.Zero;
                lock (_gate)
                {
                    _clickAge += dt;
                    if (_clickAge < 3) ripple = new Vector4(_click, _clickAge, 1);
                }

                var c = new FrameConstants
                {
                    Resolution = new Vector2(_width, _height),
                    Time = (float)time,
                    Delta = dt,
                    Mouse = _mouse,
                    MouseVel = dt > 0 ? (_mouse - prev) / dt : Vector2.Zero,
                    Intensity = 1,
                    Aspect = _width / (float)_height,
                    MouseActive = 1,
                    Interact = 1,
                    Grade = new Vector4(0, 1, 1, 0),
                    Ripple0 = ripple,
                };
                ctx!.UpdateSubresource(in c, cb);
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                ctx.VSSetShader(vs);
                ctx.PSSetConstantBuffer(0, cb);
                ctx.PSSetSampler(0, clamp);
                ctx.PSSetSampler(1, wrap);
                ctx.PSSetShaderResource(1, noiseView);
                if (baked != null) ctx.PSSetShaderResources(3, baked.Views);
                if (sim != null && simRtv != null && simSrv != null)
                {
                    ctx.PSSetShader(sim);
                    ctx.RSSetViewport(0, 0, sw, sh, 0, 1);
                    ctx.OMSetRenderTargets(simRtv[1 - simIndex], null);
                    ctx.PSSetShaderResource(2, simSrv[simIndex]);
                    ctx.Draw(3, 0);
                    ctx.PSUnsetShaderResource(2);
                    simIndex = 1 - simIndex;
                    ctx.PSSetShaderResource(2, simSrv[simIndex]);
                }
                ctx.PSSetShader(ps);
                ctx.OMSetRenderTargets(rtv, null);
                ctx.RSSetViewport(0, 0, _width, _height, 0, 1);
                ctx.Draw(3, 0);
                ctx.PSUnsetShaderResource(2);
                ctx.CopyResource(staging, target);
                var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                for (int y = 0; y < _height; y++)
                    Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), pixels, y * _width * 4, _width * 4);
                ctx.Unmap(staging, 0);

                // Hand the frame to the UI without waiting on it (Stop() joins this thread from the UI thread),
                // flipping between two buffers and dropping frames if the UI is still busy with the last one.
                if (Interlocked.CompareExchange(ref _pendingWrite, 1, 0) == 0)
                {
                    var frame = pixels;
                    pixels = ReferenceEquals(pixels, bufA) ? bufB : bufA;
                    _ui.BeginInvoke(() =>
                    {
                        if (_run) Bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _height), frame, _width * 4, 0);
                        Interlocked.Exchange(ref _pendingWrite, 0);
                    }, DispatcherPriority.Render);
                }

                // About 20 frames per second is plenty for a preview and keeps it cheap.
                int sleep = 50 - (int)((clock.Elapsed.TotalSeconds - now) * 1000);
                if (sleep > 0) Thread.Sleep(sleep);
            }
            ps?.Dispose(); sim?.Dispose(); baked?.Dispose();
            if (simTex != null) { foreach (var x in simSrv!) x.Dispose(); foreach (var x in simRtv!) x.Dispose(); foreach (var x in simTex) x.Dispose(); }
        }
        catch (Exception ex)
        {
            Log.Warn("Live preview stopped: " + ex.Message);
        }
        finally
        {
            ctx?.Dispose();
            device?.Dispose();
        }
    }

    public void Dispose() => Stop();
}
