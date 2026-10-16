using System.Runtime.InteropServices.WindowsRuntime;

using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RocoPilot.Services.Recognition;

internal static class RecognitionRegionImageHelper
{
    public static async Task<byte[]> EncodePngAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        CancellationToken cancellationToken)
    {
        var pixels = CropFrame(frame, region);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)region.Width,
            (uint)region.Height,
            96,
            96,
            pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);

        stream.Seek(0);
        var encodedLength = checked((int)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var loadedLength = await reader.LoadAsync((uint)encodedLength).AsTask(cancellationToken);
        if (loadedLength != (uint)encodedLength)
        {
            throw new InvalidDataException("区域截图编码结果读取不完整。");
        }

        var encodedBytes = new byte[encodedLength];
        reader.ReadBytes(encodedBytes);
        return encodedBytes;
    }

    public static RecognitionRegion ToFrameRegion(
        RecognitionRegion region,
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        RecognitionRegionConfig config)
    {
        var configWidth = config.ResolutionWidth > 0
            ? config.ResolutionWidth
            : targetWindow.HasClientArea ? targetWindow.ClientWidth : frame.Width;
        var configHeight = config.ResolutionHeight > 0
            ? config.ResolutionHeight
            : targetWindow.HasClientArea ? targetWindow.ClientHeight : frame.Height;

        if (configWidth <= 0 || configHeight <= 0)
        {
            return new RecognitionRegion();
        }

        _ = TryGetClientAreaInCapturedFrame(
            frame,
            targetWindow,
            out var clientX,
            out var clientY,
            out var sourceWidth,
            out var sourceHeight);

        var sourceRegion = MapReferenceArea(0, 0, sourceWidth, sourceHeight, configWidth, configHeight,
            region.X, region.Y, region.Width, region.Height, region.Anchor);

        var x = clientX + sourceRegion.X;
        var y = clientY + sourceRegion.Y;
        var right = clientX + sourceRegion.X + sourceRegion.Width;
        var bottom = clientY + sourceRegion.Y + sourceRegion.Height;
        var clampedX = ClampToRange(x, 0, frame.Width);
        var clampedY = ClampToRange(y, 0, frame.Height);
        var clampedRight = ClampToRange(right, 0, frame.Width);
        var clampedBottom = ClampToRange(bottom, 0, frame.Height);

        return clampedRight <= clampedX || clampedBottom <= clampedY
            ? new RecognitionRegion()
            : new RecognitionRegion
            {
                Id = region.Id,
                X = clampedX,
                Y = clampedY,
                Width = clampedRight - clampedX,
                Height = clampedBottom - clampedY,
                Anchor = region.Anchor,
                Enabled = region.Enabled
            };
    }

    public static bool TryGetClientAreaInCapturedFrame(
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        out int clientX,
        out int clientY,
        out int clientWidth,
        out int clientHeight)
    {
        clientX = 0;
        clientY = 0;
        clientWidth = frame.Width;
        clientHeight = frame.Height;

        if (!targetWindow.HasClientArea
            || frame.Width <= 0
            || frame.Height <= 0
            || (frame.Width == targetWindow.ClientWidth && frame.Height == targetWindow.ClientHeight))
        {
            return false;
        }

        var (frameClientOffsetX, frameClientOffsetY) = targetWindow.GetClientOffsetForFrame(
            frame.Width,
            frame.Height);
        clientX = Math.Max(0, frameClientOffsetX);
        clientY = Math.Max(0, frameClientOffsetY);

        if (clientX >= frame.Width || clientY >= frame.Height)
        {
            return false;
        }

        clientWidth = Math.Min(targetWindow.ClientWidth, frame.Width - clientX);
        clientHeight = Math.Min(targetWindow.ClientHeight, frame.Height - clientY);

        return clientWidth > 0
            && clientHeight > 0
            && (clientX != 0
                || clientY != 0
                || clientWidth != frame.Width
                || clientHeight != frame.Height);
    }

    private static byte[] CropFrame(CapturedFrame frame, RecognitionRegion region)
    {
        var expectedLength = checked(frame.Width * frame.Height * 4);
        if (frame.PixelByteLength < expectedLength)
        {
            throw new InvalidDataException("捕获帧像素数据不完整。");
        }

        var rowLength = region.Width * 4;
        var croppedPixels = new byte[region.Height * rowLength];
        for (var row = 0; row < region.Height; row++)
        {
            var sourceOffset = ((region.Y + row) * frame.Width + region.X) * 4;
            var targetOffset = row * rowLength;
            System.Buffer.BlockCopy(frame.Pixels, sourceOffset, croppedPixels, targetOffset, rowLength);
        }

        return croppedPixels;
    }

    // 控件等比缩放，画布多出的空间按锚点分配；窗口边框偏移只在最后加入。
    internal static RecognitionRegion MapReferenceArea(int clientX, int clientY, int width, int height,
        double referenceWidth, double referenceHeight, double x, double y, double regionWidth, double regionHeight,
        RecognitionAnchor anchor = RecognitionAnchor.Auto)
    {
        var scale = Math.Min(width / referenceWidth, height / referenceHeight);
        var horizontal = anchor switch
        {
            RecognitionAnchor.TopLeft or RecognitionAnchor.CenterLeft or RecognitionAnchor.BottomLeft => 0d,
            RecognitionAnchor.TopRight or RecognitionAnchor.CenterRight or RecognitionAnchor.BottomRight => 1d,
            RecognitionAnchor.Auto => AxisAnchor((x + regionWidth / 2) / referenceWidth),
            _ => .5
        };
        var vertical = anchor switch
        {
            RecognitionAnchor.TopLeft or RecognitionAnchor.TopCenter or RecognitionAnchor.TopRight => 0d,
            RecognitionAnchor.BottomLeft or RecognitionAnchor.BottomCenter or RecognitionAnchor.BottomRight => 1d,
            RecognitionAnchor.Auto => AxisAnchor((y + regionHeight / 2) / referenceHeight),
            _ => .5
        };
        var left = (int)Math.Round(x * scale + (width - referenceWidth * scale) * horizontal);
        var top = (int)Math.Round(y * scale + (height - referenceHeight * scale) * vertical);
        var right = Math.Clamp(left + (int)Math.Round(regionWidth * scale), 0, width);
        var bottom = Math.Clamp(top + (int)Math.Round(regionHeight * scale), 0, height);
        left = Math.Clamp(left, 0, width);
        top = Math.Clamp(top, 0, height);
        return new RecognitionRegion
        {
            X = clientX + left, Y = clientY + top,
            Width = Math.Max(0, right - left), Height = Math.Max(0, bottom - top),
            Anchor = anchor
        };

        // 未指定时按所在的三等分区域选择靠边或居中；布局特殊的控件显式指定锚点。
        static double AxisAnchor(double center) => center < 1d / 3 ? 0 : center > 2d / 3 ? 1 : .5;
    }

    private static int ClampToRange(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}
