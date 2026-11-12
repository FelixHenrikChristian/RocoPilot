using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using RocoPilot.Notifications;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AppNotificationServiceTests
{
    [TestMethod]
    public async Task PreferencesDefaultToEnabledAndSurviveReload()
    {
        var store = new ControlledSettingsStore();
        var service = new AppNotificationService(store, NullLogger<AppNotificationService>.Instance);
        await service.LoadSettingsAsync();
        Assert.IsTrue(service.IsEnabled);
        Assert.IsTrue(service.IsSoundEnabled);
        await service.SetEnabledAsync(false);
        await service.SetSoundEnabledAsync(false);
        var reloaded = new AppNotificationService(store, NullLogger<AppNotificationService>.Instance);
        await reloaded.LoadSettingsAsync();
        Assert.IsFalse(reloaded.IsEnabled);
        Assert.IsFalse(reloaded.IsSoundEnabled);
        Assert.IsFalse(await reloaded.ShowAsync("禁用通知", "不应触发系统通知"));
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void NotificationUsesLogoOrHeroAndRespectsSound(bool sound, bool withScreenshot)
    {
        // VSTest runs outside the application's WinRT activation manifest.
        Bootstrap.Initialize(0x00020001); // Windows App SDK 2.1
        try
        {
            VerifyNotificationLayout(sound, withScreenshot);
        }
        finally
        {
            Bootstrap.Shutdown();
        }
    }

    private static void VerifyNotificationLayout(bool sound, bool withScreenshot)
    {
        var title = "发现 <异色> & 精灵";
        var message = "已暂停\n返回游戏处理";
        var path = Path.Combine(Path.GetTempPath(), "截图 空格 & test.png");
        var toast = XDocument.Parse(AppNotificationService.BuildNotification(title, message, sound, withScreenshot ? path : null).Payload);
        CollectionAssert.AreEqual(new[] { title, message }, toast.Descendants("text").Select(x => x.Value).ToArray());
        var image = toast.Descendants("image").Single();
        Assert.AreEqual(withScreenshot ? "hero" : "appLogoOverride", (string?)image.Attribute("placement"));
        var source = new Uri(image.Attribute("src")!.Value);
        if (withScreenshot)
            Assert.AreEqual(path, source.LocalPath);
        else
        {
            Assert.AreEqual("circle", (string?)image.Attribute("hint-crop"));
            Assert.IsTrue(File.Exists(source.LocalPath), source.LocalPath);
        }
        Assert.AreEqual(sound ? null : "true", (string?)toast.Root!.Element("audio")?.Attribute("silent"));
    }

    [TestMethod]
    public async Task UninitializedNotificationReportsFailureWithoutThrowing()
    {
        var service = new AppNotificationService(new ControlledSettingsStore(), NullLogger<AppNotificationService>.Instance);
        Assert.IsFalse(await service.ShowAsync("测试通知", "未初始化"));
        Assert.IsNotNull(service.LastError);
    }
}
