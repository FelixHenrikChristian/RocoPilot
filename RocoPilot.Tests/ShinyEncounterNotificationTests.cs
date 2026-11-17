using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using RocoPilot.Models.Capture;
using RocoPilot.Notifications;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class ShinyEncounterNotificationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScreenshotContainsDetectionFrameEvenWhenSendingFails(bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RocoPilot-Shiny-" + Guid.NewGuid().ToString("N"));
        var notifications = new AppNotificationStub { Fail = fail };
        var previews = Path.Combine(directory, "previews");
        var service = new ShinyEncounterNotification(notifications, NullLogger<ShinyEncounterNotification>.Instance,
            new NotificationScreenshotStore { DirectoryPath = previews }) { ScreenshotDirectory = directory };
        // Alpha may be zero in a GDI capture: the notification must still show an opaque image.
        using var frame = new CapturedFrame(2, 1, [0, 0, 255, 0, 255, 0, 0, 0]);
        try
        {
            await service.NotifyAsync(frame);
            var message = notifications.Messages.Single();
            Assert.AreEqual("发现异色精灵", message.Title);
            Assert.IsTrue(notifications.LastOpenGame);
            // 通知用缩小的 JPEG 副本，原图仍按原尺寸保存。
            Assert.AreEqual(previews, Path.GetDirectoryName(message.ImagePath));
            using (var preview = Cv2.ImRead(message.ImagePath!, ImreadModes.Unchanged))
            {
                Assert.AreEqual(2, preview.Width);
                Assert.AreEqual(3, preview.Channels());
            }
            using var image = Cv2.ImRead(Directory.GetFiles(directory, "*.png").Single(), ImreadModes.Unchanged);
            Assert.AreEqual(2, image.Width);
            Assert.AreEqual(1, image.Height);
            Assert.AreEqual(3, image.Channels());
            Assert.AreEqual(new Vec3b(0, 0, 255), image.At<Vec3b>(0, 0));
            Assert.AreEqual(new Vec3b(255, 0, 0), image.At<Vec3b>(0, 1));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DisabledNotificationsDoNotSaveOrSendScreenshots()
    {
        var notifications = new AppNotificationStub();
        await notifications.SetEnabledAsync(false);
        var directory = Path.Combine(Path.GetTempPath(), "RocoPilot-Shiny-" + Guid.NewGuid().ToString("N"));
        var service = new ShinyEncounterNotification(notifications, NullLogger<ShinyEncounterNotification>.Instance,
            new NotificationScreenshotStore { DirectoryPath = Path.Combine(directory, "previews") })
            { ScreenshotDirectory = directory };
        using var frame = new CapturedFrame(1, 1, new byte[4]);
        await service.NotifyAsync(frame);
        Assert.IsFalse(Directory.Exists(directory));
        Assert.HasCount(0, notifications.Messages);
    }
}
