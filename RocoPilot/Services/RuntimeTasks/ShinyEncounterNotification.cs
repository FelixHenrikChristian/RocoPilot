using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Notifications;

namespace RocoPilot.Services.RuntimeTasks;

public sealed class ShinyEncounterNotification(IAppNotificationService notifications, ILogger<ShinyEncounterNotification> logger,
    NotificationScreenshotStore? screenshots = null)
{
    private readonly NotificationScreenshotStore _screenshots = screenshots ?? new();

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
            logger.LogInformation("异色截图已保存：{Path}", path);
            // 原图留给用户；通知用缩小的副本，原尺寸 PNG 可能超出通知图片的大小上限。
            var preview = await _screenshots.SaveAsync(frame);
            await notifications.ShowAsync("发现异色精灵", "本场战斗已暂停自动操作，请返回游戏处理。", preview, openGame: true);
        }
        catch (Exception ex)
        {
            // Notification errors must never undo shiny protection or stop pending-record persistence.
            logger.LogWarning(ex, "异色截图通知没有发出去，异色保护不受影响");
        }
    }
}
