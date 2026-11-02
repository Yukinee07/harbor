using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using DWrite = Vortice.DirectWrite;
using WIC = Vortice.WIC;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Harbor.Composition;

/// <summary>
/// Owns the UI-thread Windows.UI.Composition compositor plus the Direct2D/DirectWrite devices used to
/// paint into composition surfaces. Everything here must be touched from the UI thread only.
/// </summary>
internal sealed unsafe class CompositionHost : IDisposable
{
    static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    static readonly Guid IID_ID2D1DeviceContext = new("e8f7fe7a-191c-466d-ad95-975678bda998");

    static CompositionHost? _instance;
    public static CompositionHost Instance => _instance ??= new CompositionHost();

    readonly object? _queueController;
    ID3D11Device _d3d = null!;
    ID2D1Device _d2dDevice = null!;
    ID2D1Factory1 _d2dFactory = null!;

    public Compositor Compositor { get; }
    public CompositionGraphicsDevice GraphicsDevice { get; private set; } = null!;
    public DWrite.IDWriteFactory DWrite { get; }
    public WIC.IWICImagingFactory Wic { get; }

    CompositionHost()
    {
        _queueController = EnsureDispatcherQueue();
        Compositor = new Compositor();
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<DWrite.IDWriteFactory>();
        Wic = new WIC.IWICImagingFactory();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        CreateDevices();
    }

    void CreateDevices()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        // This device only paints icons and labels into surfaces, a few small ones at a time and only when they change.
        // Software does that without a GPU driver in the process, which costs about 50 MB per device on current Intel
        // drivers. All motion is animated by the system compositor either way, so it isn't any less smooth.
        if (D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device, out ID3D11DeviceContext? context).Success && device != null)
        {
            _d3d = device;
            context!.Dispose();
        }
        else
        {
            Harbor.Interop.Gpu.CreateDevice(DeviceCreationFlags.BgraSupport, levels, out _d3d, out var hardwareContext);
            hardwareContext.Dispose();
        }
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgi);

        IntPtr compositorPtr = ((IWinRTObject)Compositor).NativeObject.ThisPtr;
        var iid = IID_ICompositorInterop;
        Marshal.QueryInterface(compositorPtr, in iid, out IntPtr interop).CheckHr();
        try
        {
            IntPtr result;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(*(void***)interop + 5));
            create(interop, _d2dDevice.NativePointer, &result).CheckHr();
            GraphicsDevice = CompositionGraphicsDevice.FromAbi(result);
            Marshal.Release(result);
        }
        finally { Marshal.Release(interop); }
    }

    public ID2D1DeviceContext CreateDeviceContext()
    {
        var dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        dc.SetDpi(96, 96);
        return dc;
    }

    public DesktopWindowTarget CreateDesktopWindowTarget(IntPtr hwnd, bool topmost)
    {
        IntPtr compositorPtr = ((IWinRTObject)Compositor).NativeObject.ThisPtr;
        var iid = IID_ICompositorDesktopInterop;
        Marshal.QueryInterface(compositorPtr, in iid, out IntPtr interop).CheckHr();
        try
        {
            IntPtr result;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(*(void***)interop + 3));
            create(interop, hwnd, topmost ? 1 : 0, &result).CheckHr();
            var target = DesktopWindowTarget.FromAbi(result);
            Marshal.Release(result);
            return target;
        }
        finally { Marshal.Release(interop); }
    }

    public CompositionDrawingSurface CreateSurface(int width, int height) =>
        GraphicsDevice.CreateDrawingSurface(new Windows.Foundation.Size(Math.Max(1, width), Math.Max(1, height)),
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Windows.Graphics.DirectX.DirectXAlphaMode.Premultiplied);

    /// <summary>Paints the whole surface. The callback draws in surface pixel coordinates starting at (0,0).</summary>
    public void Draw(CompositionDrawingSurface surface, Action<ID2D1DeviceContext> paint)
    {
        IntPtr surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var iid = IID_ICompositionDrawingSurfaceInterop;
        Marshal.QueryInterface(surfacePtr, in iid, out IntPtr interop).CheckHr();
        try
        {
            var vtbl = *(void***)interop;
            var beginDraw = (delegate* unmanaged[Stdcall]<IntPtr, RawRect*, Guid*, IntPtr*, Vortice.Mathematics.Int2*, int>)vtbl[3];
            var endDraw = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[4];

            Guid dcIid = IID_ID2D1DeviceContext;
            IntPtr dcPtr;
            Vortice.Mathematics.Int2 offset;
            int hr = beginDraw(interop, null, &dcIid, &dcPtr, &offset);
            if (hr == unchecked((int)0x887A0005) /* DXGI_ERROR_DEVICE_REMOVED */ || hr == unchecked((int)0x887A0007))
            {
                RecreateDevices();
                return;
            }
            hr.CheckHr();

            using (var dc = new ID2D1DeviceContext(dcPtr))
            {
                var size = surface.SizeInt32;
                dc.SetDpi(96, 96);
                dc.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);
                dc.PushAxisAlignedClip(new RawRectF(0, 0, size.Width, size.Height), AntialiasMode.Aliased);
                dc.Clear(new Color4(0, 0, 0, 0));
                try { paint(dc); }
                finally { dc.PopAxisAlignedClip(); }
            }
            endDraw(interop);
        }
        finally { Marshal.Release(interop); }
    }

    public event Action? DeviceReplaced;

    void RecreateDevices()
    {
        _d2dDevice.Dispose();
        _d3d.Dispose();
        CreateDevices();
        DeviceReplaced?.Invoke();
    }

    static object? EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return null;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 2,  // DQTAT_COM_STA
        };
        CreateDispatcherQueueController(options, out IntPtr controller).CheckHr();
        return Windows.System.DispatcherQueueController.FromAbi(controller);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    public void Dispose()
    {
        GraphicsDevice?.Dispose();
        _d2dDevice?.Dispose();
        _d3d?.Dispose();
        _d2dFactory?.Dispose();
        Wic.Dispose();
        DWrite.Dispose();
        Compositor.Dispose();
        GC.KeepAlive(_queueController);
    }
}

internal static class HrExtensions
{
    public static void CheckHr(this int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
