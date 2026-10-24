using System.Xml.Linq;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Notifications;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AppNotificationServiceTests
{
    [TestMethod]
    public async Task NotificationPipeAllowsTheAccountSidRegardlessOfElevation()
    {
        var name = "RocoPilot.Notifications.Test." + Guid.NewGuid().ToString("N");
        using var server = AppNotificationService.CreateNotificationPipe(name);
        using var identity = WindowsIdentity.GetCurrent();
        var security = server.GetAccessControl();
        Assert.AreEqual(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.HasCount(1, rules);
        Assert.AreEqual(identity.User, rules[0].IdentityReference);
        Assert.AreEqual(AccessControlType.Allow, rules[0].AccessControlType);
        Assert.AreEqual(PipeAccessRights.FullControl, rules[0].PipeAccessRights);

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        var received = new byte[1];
        await Task.WhenAll(client.WriteAsync(new byte[] { 42 }, timeout.Token).AsTask(),
            server.ReadExactlyAsync(received, timeout.Token).AsTask());
        Assert.AreEqual((byte)42, received[0]);
    }

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
