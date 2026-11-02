using System.Runtime.InteropServices;

namespace Harbor.Interop;

/// <summary>Premultiplied BGRA pixels, top-down rows.</summary>
internal sealed class IconImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>
/// High resolution icons through IShellItemImageFactory, which covers .exe/.lnk files and
/// shell:AppsFolder entries (packaged apps) with the same code path.
/// </summary>
internal static class ShellIcons
{
    [Flags]
    enum SIIGBF : uint
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        ScaleUp = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Loads an icon for a file path or a shell parsing name (e.g. "shell:AppsFolder\{AUMID}").</summary>
    public static IconImage? Load(string parsingName, int size = 256)
    {
        try
        {
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_IShellItemImageFactory, out var factory) < 0 || factory is null)
                return null;
            try
            {
                var sz = new SIZE { cx = size, cy = size };
                if (factory.GetImage(sz, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out IntPtr hbm) < 0 || hbm == IntPtr.Zero)
                    return null;
                try { return FromHBitmap(hbm); }
                finally { Native.DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    /// <summary>Converts a window/class HICON (does not take ownership of it).</summary>
    public static IconImage? FromHIcon(IntPtr hicon)
    {
        if (hicon == IntPtr.Zero || !GetIconInfo(hicon, out var info)) return null;
        try
        {
            return info.hbmColor == IntPtr.Zero ? null : FromHBitmap(info.hbmColor);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
        }
    }

    static IconImage? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bmp) == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var px = new byte[w * h * 4];
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bmi, 0) == 0) return null;
        }
        finally { DeleteDC(dc); }

        bool anyAlpha = false, premultiplied = true;
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            if (a != 0) anyAlpha = true;
            if (px[i] > a || px[i + 1] > a || px[i + 2] > a) { premultiplied = false; }
        }
        if (!anyAlpha)
        {
            // 24-bit style icon without an alpha channel: treat it as opaque.
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        }
        else if (!premultiplied)
        {
            Premultiply(px);
        }
        return Trim(new IconImage(w, h, px));
    }

    static void Premultiply(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            px[i] = (byte)(px[i] * a / 255);
            px[i + 1] = (byte)(px[i + 1] * a / 255);
            px[i + 2] = (byte)(px[i + 2] * a / 255);
        }
    }

    /// <summary>
    /// Legacy apps often only ship a 32/48px icon which the shell centers inside a 256px canvas.
    /// Crop to the visible content so the dock can scale it to the same visual size as modern icons.
    /// </summary>
    static IconImage Trim(IconImage img)
    {
        int minX = img.Width, minY = img.Height, maxX = -1, maxY = -1;
        var px = img.Pixels;
        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            if (px[(y * img.Width + x) * 4 + 3] < 8) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return img;
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        // Only crop when the content is clearly a small icon floating in a big canvas.
        if (Math.Max(cw, ch) > img.Width * 0.6) return img;
        int side = Math.Max(cw, ch);
        int pad = Math.Max(1, side / 16);
        side += pad * 2;
        int ox = minX - pad - (side - pad * 2 - cw) / 2, oy = minY - pad - (side - pad * 2 - ch) / 2;
        var outPx = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            int sy = oy + y;
            if (sy < 0 || sy >= img.Height) continue;
            for (int x = 0; x < side; x++)
            {
                int sx = ox + x;
                if (sx < 0 || sx >= img.Width) continue;
                Buffer.BlockCopy(px, (sy * img.Width + sx) * 4, outPx, (y * side + x) * 4, 4);
            }
        }
        return new IconImage(side, side, outPx);
    }
}
