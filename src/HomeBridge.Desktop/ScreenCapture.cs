using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using HomeBridge.Core;

namespace HomeBridge.Desktop;

public static class ScreenCapture
{
    public static byte[] PrimaryScreen()
    {
        var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new IOException("Primary screen is unavailable.");
        var scale = Math.Min(1.0, Math.Min((double)ScreenFrames.MaximumWidth / width, (double)ScreenFrames.MaximumHeight / height));
        var targetWidth = Math.Max(1, (int)(width * scale)); var targetHeight = Math.Max(1, (int)(height * scale));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception("Cannot capture the screen.");
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, targetWidth, targetHeight);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new IOException("Screen capture resources are unavailable.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new IOException("Screen bitmap selection failed.");
            SetStretchBltMode(memory, 4); // HALFTONE
            if (!StretchBlt(memory, 0, 0, targetWidth, targetHeight, screen, 0, 0, width, height, 0x40CC0020))
                throw new IOException("Screen capture failed. Locked/secure desktops are unsupported.");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 65 }; encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream(); encoder.Save(output);
            var jpeg = output.ToArray(); ScreenFrames.ValidateJpeg(jpeg); return jpeg;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, uint operation);
}
