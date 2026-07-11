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

        var sourceRegion = new RecognitionRegion
        {
            Id = region.Id,
            X = ScaleValue(region.X, configWidth, sourceWidth),
            Y = ScaleValue(region.Y, configHeight, sourceHeight),
            Width = Math.Max(1, ScaleValue(region.Width, configWidth, sourceWidth)),
            Height = Math.Max(1, ScaleValue(region.Height, configHeight, sourceHeight)),
            Enabled = region.Enabled
        };

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

    private static int ScaleValue(int value, int sourceSize, int targetSize)
    {
        if (sourceSize <= 0 || targetSize <= 0)
        {
            return value;
        }

        return (int)Math.Round(value * (double)targetSize / sourceSize);
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
