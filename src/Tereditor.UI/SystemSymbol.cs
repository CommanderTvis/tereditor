using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Tereditor.UI;

/// <summary>
/// Asks macOS (11 and later) for an SF Symbol and renders it as a black-on-transparent bitmap, the shape the native menu tints as a
/// template image. The glyph comes from the system at run time, so nothing of SF Symbols ships with the app. It calls the Objective-C
/// runtime directly, so it needs no native library; any failure (an older system, a missing symbol, a missing library) gives null.
/// </summary>
public static class SystemSymbol
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const long PngFileType = 4;
    private const long SourceOver = 2;

    [StructLayout(LayoutKind.Sequential)] private struct Size2 { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public double X, Y, Width, Height; }

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, long a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendBitmap(nint receiver, nint selector, nint planes, long width, long height,
        long bitsPerSample, long samplesPerPixel, byte hasAlpha, byte isPlanar, nint colorSpace, long bytesPerRow, long bitsPerPixel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Size2 SendSize(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetSize(nint receiver, nint selector, Size2 size);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendDraw(nint receiver, nint selector, Rect to, Rect from, long operation, double fraction);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(nint receiver, nint selector);

    /// <summary>The symbol (for example "gearshape") at <paramref name="points"/> points and <paramref name="scale"/> pixels per point, or null when macOS cannot provide it.</summary>
    public static Bitmap? Render(string name, int points, int scale)
    {
        if (!OperatingSystem.IsMacOS() || !OperatingSystem.IsMacOSVersionAtLeast(11)) return null;
        try { return RenderCore(name, points, scale); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static Bitmap? RenderCore(string name, int points, int scale)
    {
        NativeLibrary.Load(AppKit);
        nint Sel(string selector) => sel_registerName(selector);
        nint Text(string value) => Send(objc_getClass("NSString"), Sel("stringWithUTF8String:"), Marshal.StringToCoTaskMemUTF8(value));
        var image = Send(objc_getClass("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), Text(name), 0);
        if (image == 0) return null;
        var pixels = points * scale;
        var rep = SendBitmap(Send(objc_getClass("NSBitmapImageRep"), Sel("alloc")), Sel("initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:samplesPerPixel:hasAlpha:isPlanar:colorSpaceName:bytesPerRow:bitsPerPixel:"),
            0, pixels, pixels, 8, 4, 1, 0, Text("NSCalibratedRGBColorSpace"), 0, 0);
        if (rep == 0) return null;
        var context = Send(objc_getClass("NSGraphicsContext"), Sel("graphicsContextWithBitmapImageRep:"), rep);
        if (context == 0) return null;
        Send(objc_getClass("NSGraphicsContext"), Sel("saveGraphicsState"));
        Send(objc_getClass("NSGraphicsContext"), Sel("setCurrentContext:"), context);
        var natural = SendSize(image, Sel("size"));
        if (natural.Width <= 0 || natural.Height <= 0) return null;
        // Fit the glyph into the square, centered, as the system menu does.
        var fit = Math.Min(pixels / natural.Width, pixels / natural.Height);
        var width = natural.Width * fit;
        var height = natural.Height * fit;
        SendDraw(image, Sel("drawInRect:fromRect:operation:fraction:"), new Rect { X = (pixels - width) / 2, Y = (pixels - height) / 2, Width = width, Height = height }, default, SourceOver, 1);
        Send(objc_getClass("NSGraphicsContext"), Sel("restoreGraphicsState"));
        var data = Send(rep, Sel("representationUsingType:properties:"), PngFileType, 0);
        if (data == 0) return null;
        var length = (int)SendLong(data, Sel("length"));
        var bytes = new byte[length];
        Marshal.Copy(Send(data, Sel("bytes")), bytes, 0, length);
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        // The decoded bitmap is in pixels at 96 dpi; the menu wants points, so copy it into one that says it is at the scale.
        var result = new WriteableBitmap(decoded.PixelSize, new Vector(96 * scale, 96 * scale), decoded.Format ?? PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = result.Lock();
        decoded.CopyPixels(new PixelRect(decoded.PixelSize), buffer.Address, buffer.RowBytes * buffer.Size.Height, buffer.RowBytes);
        return result;
    }
}