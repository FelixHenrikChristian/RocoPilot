using System.Runtime.InteropServices;
using OpenCvSharp;
using RocoPilot.Models.Capture;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Notifications;

/// <summary>
/// 通知配图：只留游戏客户区，缩小后存成 JPEG，目录里只保留最近若干张。
/// 这里的图只给通知用，不是给用户留存的截图。
/// </summary>
public sealed class NotificationScreenshotStore
{
    // 通知大图最多按 728×360 显示，再大只会多占磁盘。
    internal const int MaxEdge = 1024;
    internal const int MaxFiles = 20;

    internal string DirectoryPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocoPilot", "Screenshots", "Notifications");

    public async Task<string> SaveAsync(CapturedFrame frame, CaptureTargetWindow? window = null,
        CancellationToken cancellationToken = default)
    {
        var jpeg = Encode(frame, window);
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, $"{frame.CapturedAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, jpeg, cancellationToken);
        Prune(path);
        return path;
    }

    internal static byte[] Encode(CapturedFrame frame, CaptureTargetWindow? window)
    {
        int x = 0, y = 0, width = frame.Width, height = frame.Height;
        if (window is not null)
            _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window, out x, out y, out width, out height);

        var pixels = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, pixels.AddrOfPinnedObject());
            using var client = new Mat(bgra, new Rect(x, y, width, height));
            // GDI 截图的透明通道可能全为 0，丢掉它才不会得到一张透明的图。
            using var bgr = new Mat();
            Cv2.CvtColor(client, bgr, ColorConversionCodes.BGRA2BGR);
            var scale = Math.Min(1d, (double)MaxEdge / Math.Max(width, height));
            using var output = new Mat();
            if (scale < 1)
                Cv2.Resize(bgr, output, new Size(Math.Max(1, (int)Math.Round(width * scale)),
                    Math.Max(1, (int)Math.Round(height * scale))), interpolation: InterpolationFlags.Area);
            else
                bgr.CopyTo(output);
            Cv2.ImEncode(".jpg", output, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
            return jpeg;
        }
        finally { pixels.Free(); }
    }

    private void Prune(string keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(DirectoryPath).GetFiles("*.jpg")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaxFiles))
                if (!string.Equals(old.FullName, keep, StringComparison.OrdinalIgnoreCase)) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败只是多留几张图，下次保存时再试。
        }
    }
}
