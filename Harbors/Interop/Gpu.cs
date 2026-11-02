using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Harbor.Interop;

/// <summary>
/// Every Direct3D device in Harbor goes through here. Passing no adapter leaves the choice to Windows, and on a
/// laptop with two GPUs a device on the discrete one keeps it powered, which costs far more battery than anything
/// the dock draws. A dock and a wallpaper never need that GPU, so ask for the power-saving adapter by name.
/// </summary>
internal static class Gpu
{
    public static void CreateDevice(DeviceCreationFlags flags, FeatureLevel[] levels, out ID3D11Device device, out ID3D11DeviceContext context)
    {
        ID3D11Device? d = null;
        ID3D11DeviceContext? c = null;
        using (var adapter = LowPowerAdapter())
        {
            if (adapter != null && D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels, out d, out c).Success && d != null)
            {
                device = d;
                context = c!;
                return;
            }
        }
        if (D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, flags, levels, out d, out c).Failure || d == null)
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out d, out c).CheckError();
        device = d!;
        context = c!;
    }

    static IDXGIAdapter1? LowPowerAdapter()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            return factory.EnumAdapterByGpuPreference(0, GpuPreference.MinimumPower, out IDXGIAdapter1? adapter).Success ? adapter : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask for the power-saving GPU: " + ex.Message);
            return null;
        }
    }
}
