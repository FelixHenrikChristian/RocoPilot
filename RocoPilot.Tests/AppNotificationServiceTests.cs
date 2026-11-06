using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    [DataRow(true)]
    [DataRow(false)]
    public void PayloadPreservesTextAndOptionalScreenshot(bool sound)
    {
        var title = "发现 <异色> & 精灵";
        var message = "已暂停\n返回游戏处理";
        var path = Path.Combine(Path.GetTempPath(), "截图 空格 & test.png");
        var toast = XDocument.Parse(AppNotificationService.BuildPayload(title, message, sound, path));
        CollectionAssert.AreEqual(new[] { title, message }, toast.Descendants("text").Select(x => x.Value).ToArray());
        Assert.AreEqual(new Uri(path).AbsoluteUri, toast.Descendants("image").Single().Attribute("src")!.Value);
        Assert.AreEqual(sound ? null : "true", (string?)toast.Root!.Element("audio")?.Attribute("silent"));
        Assert.IsFalse(XDocument.Parse(AppNotificationService.BuildPayload(title, message, sound)).Descendants("image").Any());
    }

    [TestMethod]
    public async Task UninitializedNotificationReportsFailureWithoutThrowing()
    {
        var service = new AppNotificationService(new ControlledSettingsStore(), NullLogger<AppNotificationService>.Instance);
        Assert.IsFalse(await service.ShowAsync("测试通知", "未初始化"));
        Assert.IsNotNull(service.LastError);
    }
}
