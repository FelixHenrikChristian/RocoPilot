using System.Runtime.InteropServices;

using RocoPilot.Models.Capture;

namespace RocoPilot.Services.Capture.Backends;

public abstract class GdiWindowCaptureBackendBase : ICaptureBackend
{
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    public abstract CaptureMethod Method
    {
        get;
    }

    public CapturedFrame? Capture(CaptureTargetWindow targetWindow)
    {
        if (targetWindow.Hwnd == IntPtr.Zero)
        {
            return null;
        }

        return CaptureWindow(targetWindow.Hwnd, RenderWindow);
    }

    public void Release(CaptureTargetWindow targetWindow)
    {
    }

    protected abstract bool RenderWindow(IntPtr hwnd, IntPtr memoryDc, int width, int height);

    protected static IntPtr GetTargetWindowDc(IntPtr hwnd) => GetWindowDC(hwnd);

    protected static int ReleaseTargetWindowDc(IntPtr hwnd, IntPtr hdc) => ReleaseDC(hwnd, hdc);

    private static CapturedFrame? CaptureWindow(IntPtr hwnd, WindowRenderAction render)
    {
        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var width = rect.Width;
        var height = rect.Height;
        var windowDc = GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousObject = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(windowDc);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            previousObject = SelectObject(memoryDc, bitmap);

            if (!render(hwnd, memoryDc, width, height))
            {
                return null;
            }

            CapturedFrame? capturedFrame = null;
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb
                }
            };

            capturedFrame = CapturedFrame.RentBgra32(width, height);
            var scanLines = GetDIBits(
                memoryDc,
                bitmap,
                0,
                (uint)height,
                capturedFrame.Pixels,
                ref bitmapInfo,
                DibRgbColors);
            if (scanLines == 0)
            {
                capturedFrame.Dispose();
                return null;
            }

            ForceOpaqueAlpha(capturedFrame.Pixels, capturedFrame.PixelByteLength);
            return capturedFrame;
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previousObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }

            _ = ReleaseDC(hwnd, windowDc);
        }
    }

    private static void ForceOpaqueAlpha(byte[] pixels, int pixelByteLength)
    {
        for (var i = 3; i < pixelByteLength; i += 4)
        {
            pixels[i] = 255;
        }
    }

    private delegate bool WindowRenderAction(IntPtr hwnd, IntPtr memoryDc, int width, int height);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr bitmap,
        uint start,
        uint lines,
        byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }
}
