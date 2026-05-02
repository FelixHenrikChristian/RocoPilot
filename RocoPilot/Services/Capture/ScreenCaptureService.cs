using System.Runtime.InteropServices;

using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;

namespace RocoPilot.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint PrintWindowDefault = 0;
    private const uint PrintWindowRenderFullContent = 2;

    public CapturedFrame? Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
    {
        if (targetWindow.Hwnd == IntPtr.Zero)
        {
            return null;
        }

        return method switch
        {
            CaptureMethod.PrintWindow => CaptureWithPrintWindow(targetWindow.Hwnd),
            _ => CaptureWithBitBlt(targetWindow.Hwnd)
        };
    }

    private static CapturedFrame? CaptureWithBitBlt(IntPtr hwnd)
    {
        return CaptureWindow(hwnd, static (targetHwnd, memoryDc, width, height) =>
        {
            var sourceDc = GetWindowDC(targetHwnd);
            if (sourceDc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return BitBlt(memoryDc, 0, 0, width, height, sourceDc, 0, 0, Srccopy | Captureblt);
            }
            finally
            {
                _ = ReleaseDC(targetHwnd, sourceDc);
            }
        });
    }

    private static CapturedFrame? CaptureWithPrintWindow(IntPtr hwnd)
    {
        return CaptureWindow(hwnd, static (targetHwnd, memoryDc, _, _) =>
        {
            return PrintWindow(targetHwnd, memoryDc, PrintWindowRenderFullContent)
                || PrintWindow(targetHwnd, memoryDc, PrintWindowDefault);
        });
    }

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

            var pixels = new byte[width * height * 4];
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

            var scanLines = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref bitmapInfo, DibRgbColors);
            if (scanLines == 0)
            {
                return null;
            }

            ForceOpaqueAlpha(pixels);
            return new CapturedFrame(width, height, pixels);
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

    private static void ForceOpaqueAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
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

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdc,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceHdc,
        int sourceX,
        int sourceY,
        int rasterOperation);

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
