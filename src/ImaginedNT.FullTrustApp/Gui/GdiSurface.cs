using System.ComponentModel;
using System.Runtime.InteropServices;
using ImaginedNT.App.Gui;

namespace ImaginedNT.FullTrustApp.Gui;

internal sealed record GdiFrame(int Width, int Height, byte[] Pixels, long Version);

internal sealed class GdiSurface : IDisposable
{
    public nint Dc { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    private nint _bitmap;
    private nint _original;
    private nint _bits;
    private long _version;
    public GdiFrame? Frame { get; private set; }

    public GdiSurface(int width, int height)
    {
        Dc = GdiNative.CreateCompatibleDC(0);
        if (Dc == 0) throw new Win32Exception();
        try { Resize(width, height); }
        catch { Dispose(); throw; }
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (Width == width && Height == height) return;
        var size = checked(width * height * 4);
        var info = new GdiNative.BitmapInfo
        {
            Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
            SizeImage = (uint)size
        };
        var bitmap = GdiNative.CreateDIBSection(Dc, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0) throw new Win32Exception();
        GdiNative.GdiFlush();
        var previous = GdiNative.SelectObject(Dc, bitmap);
        if (previous == 0 || previous == -1)
        {
            GdiNative.DeleteObject(bitmap);
            throw new Win32Exception();
        }
        if (_bitmap == 0) _original = previous;
        else GdiNative.DeleteObject(_bitmap);
        _bitmap = bitmap;
        _bits = bits;
        Width = width;
        Height = height;
        Frame = null;
    }

    public unsafe void Publish()
    {
        // Flush on the guest thread before reading DIB bits; XAML only reads this copy.
        GdiNative.GdiFlush();
        if (Frame is not null)
        {
            var source = new ReadOnlySpan<uint>((void*)_bits, checked(Width * Height));
            var previous = MemoryMarshal.Cast<byte, uint>(Frame.Pixels.AsSpan());
            var changed = false;
            for (var i = 0; i < source.Length; i++)
                if ((source[i] & 0xFFFFFF) != (previous[i] & 0xFFFFFF))
                {
                    changed = true;
                    break;
                }
            if (!changed) return;
        }
        var pixels = new byte[checked(Width * Height * 4)];
        Marshal.Copy(_bits, pixels, 0, pixels.Length);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        Frame = new(Width, Height, pixels, ++_version);
    }

    public void Dispose()
    {
        if (Dc == 0) return;
        if (_bitmap != 0)
        {
            GdiNative.SelectObject(Dc, _original);
            GdiNative.DeleteObject(_bitmap);
        }
        GdiNative.DeleteDC(Dc);
        _bitmap = 0;
        Dc = 0;
    }
}

internal static class GdiNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant, Color;
    }

    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
    [DllImport("gdi32.dll")] internal static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32.dll")] internal static extern int IntersectClipRect(nint dc, int left, int top, int right, int bottom);
    [DllImport("user32.dll")] internal static extern nint GetSysColorBrush(int index);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
}
