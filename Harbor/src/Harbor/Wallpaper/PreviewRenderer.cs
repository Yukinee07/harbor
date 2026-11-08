using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Harbor.Wallpaper;

/// <summary>Renders a still of a scene offscreen, for the settings gallery.</summary>
internal static class PreviewRenderer
{
    /// <summary>Renders each scene in turn on one device, which is created for the batch and released after it.</summary>
    public static void RenderEach(IEnumerable<string> scenes, int width, int height, float time, Action<string, byte[]> done)
    {
        try
        {
            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
            Harbor.Interop.Gpu.CreateDevice(DeviceCreationFlags.BgraSupport, levels, out var device, out var ctx);
            using var _d = device;
            using var _c = ctx;
            foreach (var scene in scenes)
                if (Render(device, ctx, scene, width, height, time) is { } pixels) done(scene, pixels);
        }
        catch (Exception ex)
        {
            Log.Warn("Previews failed: " + ex.Message);
        }
    }

    static byte[]? Render(ID3D11Device device, ID3D11DeviceContext ctx, string sceneId, int width, int height, float time)
    {
        try
        {
            string common = WallpaperEngine.ReadResource("Common.hlsl");
            string tail = WallpaperEngine.ReadResource("CommonMain.hlsl");
            string stub = common + "\nfloat3 scene(float2 uv, float2 p) { return 0; }\n" + tail;
            using var vs = device.CreateVertexShader(WallpaperEngine.Compile(stub, "VSMain", "vs_5_0", "common"));
            using var ps = device.CreatePixelShader(WallpaperEngine.Compile(
                common + "\n" + WallpaperEngine.ReadResource(sceneId + ".hlsl") + "\n" + tail, "PSMain", "ps_5_0", sceneId));
            using var cb = device.CreateBuffer((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.None, 0);

            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget,
                ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            using var target = device.CreateTexture2D(desc);
            using var rtv = device.CreateRenderTargetView(target, null);
            var stagingDesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None);
            using var staging = device.CreateTexture2D(stagingDesc);

            using var baked = SceneCatalog.Get(sceneId).UsesBake ? SakuraPainter.Bake(device, width) : null;

            // A posed cursor so the preview shows the interactive effect.
            var c = new FrameConstants
            {
                Resolution = new Vector2(width, height),
                Time = time,
                Mouse = new Vector2(0.64f, 0.42f),
                Intensity = 1,
                Aspect = width / (float)height,
                MouseActive = 1,
                Grade = new Vector4(0, 1, 1, 0),
            };
            ctx.UpdateSubresource(in c, cb);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(vs);
            ctx.PSSetShader(ps);
            ctx.PSSetConstantBuffer(0, cb);
            var (noise, noiseView) = WallpaperEngine.CreateNoiseTexture(device);
            using var _n = noise;
            using var _nv = noiseView;
            using var wrap = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
            ctx.PSSetShaderResource(1, noiseView);
            ctx.PSSetSampler(1, wrap);
            if (baked != null) ctx.PSSetShaderResources(3, baked.Views);
            var info = SceneCatalog.Get(sceneId);
            ID3D11Texture2D[]? sim = null;
            ID3D11RenderTargetView[]? simRtv = null;
            ID3D11ShaderResourceView[]? simSrv = null;
            if (info.UsesSimulation)
            {
                int sw = width / 3, sh = height / 3;
                using var simPs = device.CreatePixelShader(WallpaperEngine.Compile(
                    common + "\n" + WallpaperEngine.ReadResource(sceneId + ".hlsl") + "\n" + tail, "PSSim", "ps_5_0", sceneId + "-sim"));
                var sd = new Texture2DDescription(Format.R16G16_Float, (uint)sw, (uint)sh, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource,
                    ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
                sim = new[] { device.CreateTexture2D(sd), device.CreateTexture2D(sd) };
                simRtv = sim.Select(x => device.CreateRenderTargetView(x, null)).ToArray();
                simSrv = sim.Select(x => device.CreateShaderResourceView(x, null)).ToArray();
                foreach (var r in simRtv) ctx.ClearRenderTargetView(r, new Color4(0, 0, 0, 0));
                ctx.PSSetShader(simPs);
                ctx.RSSetViewport(0, 0, sw, sh, 0, 1);
                int idx = 0;
                for (int step = 0; step < 150; step++)
                {
                    // A cursor sweeping across the water with two clicks along the way.
                    float k = step / 150f;
                    var sc = c;
                    sc.Time = time + step / 60f;
                    sc.Mouse = new Vector2(0.2f + 0.6f * k, 0.55f + 0.15f * MathF.Sin(k * 6f));
                    sc.MouseVel = new Vector2(0.6f, 0.15f * 6f * MathF.Cos(k * 6f) / 2.5f);
                    sc.Ripple0 = step == 40 ? new Vector4(0.35f, 0.4f, 0, 1) : Vector4.Zero;
                    sc.Ripple1 = step == 110 ? new Vector4(0.7f, 0.62f, 0, 1) : Vector4.Zero;
                    ctx.UpdateSubresource(in sc, cb);
                    ctx.OMSetRenderTargets(simRtv[1 - idx], null);
                    ctx.PSSetShaderResource(2, simSrv[idx]);
                    ctx.Draw(3, 0);
                    ctx.PSUnsetShaderResource(2);
                    idx = 1 - idx;
                }
                ctx.PSSetShader(ps);
                ctx.UpdateSubresource(in c, cb);
                ctx.PSSetShaderResource(2, simSrv[idx]);
            }

            ctx.OMSetRenderTargets(rtv, null);
            ctx.RSSetViewport(0, 0, width, height, 0, 1);
            ctx.Draw(3, 0);
            if (sim != null)
            {
                foreach (var x in simSrv!) x.Dispose();
                foreach (var x in simRtv!) x.Dispose();
                foreach (var x in sim) x.Dispose();
            }
            ctx.CopyResource(staging, target);

            var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), pixels, y * width * 4, width * 4);
            ctx.Unmap(staging, 0);
            return pixels;
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview for {sceneId} failed: {ex.Message}");
            return null;
        }
    }
}
