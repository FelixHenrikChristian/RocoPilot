using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;

namespace RocoPilot.Services.RuntimeTasks;

public sealed class ShinyEncounterNotification(IAppNotificationService notifications, ILogger<ShinyEncounterNotification> logger)
{
    internal string ScreenshotDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocoPilot", "Screenshots", "Shiny");

    public async Task NotifyAsync(CapturedFrame frame)
    {
        if (!notifications.IsEnabled) return;
        try
        {
            Directory.CreateDirectory(ScreenshotDirectory);
            var path = Path.Combine(ScreenshotDirectory, $"{frame.CapturedAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
            byte[] png;
            var pixels = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            try
            {
                using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, pixels.AddrOfPinnedObject());
                using var bgr = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                Cv2.ImEncode(".png", bgr, out png);
            }
            finally { pixels.Free(); }
            await File.WriteAllBytesAsync(path, png);
            if (!await notifications.ShowAsync("发现异色精灵", "本场战斗已暂停自动操作，请返回游戏处理。", path))
                logger.LogWarning("异色通知发送失败，截图已保存：{Path}", path);
        }
        catch (Exception ex)
        {
            // Notification errors must never undo shiny protection or stop pending-record persistence.
            logger.LogWarning(ex, "发送异色截图通知失败。");
        }
    }
}
