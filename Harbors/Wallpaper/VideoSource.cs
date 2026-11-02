using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Harbor.Wallpaper;

/// <summary>
/// Hardware-decoded, looping, muted video through IMFMediaEngine in frame-server mode. Frames are
/// copied GPU-side into a texture the scene shader samples, so 4K playback costs almost no CPU.
/// </summary>
internal sealed class VideoSource : IDisposable
{
    static bool s_started;

    readonly IMFDXGIDeviceManager _manager;
    readonly IMFMediaEngine _engine;
    readonly MediaEngineNotifyDelegate _callback;
    ID3D11Texture2D? _texture;
    volatile bool _metadataReady;
    volatile bool _failed;

    public ID3D11ShaderResourceView? View { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Failed => _failed;

    public VideoSource(ID3D11Device device, string path)
    {
        if (!s_started)
        {
            MediaFactory.MFStartup(true).CheckError();
            s_started = true;
        }
        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(device).CheckError();

        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(MediaEngineAttributeKeys.DxgiManager.Guid, _manager);
        attributes.Set(MediaEngineAttributeKeys.VideoOutputFormat.Guid, (uint)Format.B8G8R8A8_UNorm);

        _callback = OnEngineEvent;
        using var factory = new IMFMediaEngineClassFactory();
        _engine = factory.CreateInstance(MediaEngineCreateFlags.None, attributes, _callback);
        _engine.Muted = true;
        _engine.Loop = true;
        _engine.AutoPlay = true;
        _engine.SetSource(new Uri(path).AbsoluteUri).CheckError();
    }

    void OnEngineEvent(MediaEngineEvent e, UIntPtr param1, int param2)
    {
        switch (e)
        {
            case MediaEngineEvent.LoadedMetadata:
                _metadataReady = true;
                break;
            case MediaEngineEvent.Error:
                _failed = true;
                Log.Warn($"Video playback error {param1} / 0x{param2:X8}");
                break;
        }
    }

    /// <summary>Copies the newest decoded frame into <see cref="View"/>. Returns true if a new frame arrived.</summary>
    public bool Update(ID3D11Device device)
    {
        if (_failed) return false;
        if (_texture == null)
        {
            if (!_metadataReady) return false;
            _engine.GetNativeVideoSize(out int w, out int h);
            if (w <= 0 || h <= 0) return false;
            Width = w;
            Height = h;
            _texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            View = device.CreateShaderResourceView(_texture, null);
            Log.Info($"Video ready: {w}x{h}");
        }
        if (!_engine.OnVideoStreamTick(out _)) return false;
        _engine.TransferVideoFrame(_texture, null, new RawRect(0, 0, Width, Height), null);
        return true;
    }

    public void Pause() { try { _engine.Pause(); } catch { } }
    public void Play() { try { _engine.Play(); } catch { } }

    public void Dispose()
    {
        try { _engine.Shutdown(); } catch { }
        _engine.Dispose();
        View?.Dispose();
        _texture?.Dispose();
        _manager.Dispose();
    }
}
