using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Hotkeys;
using RocoPilot.Services;
using RocoPilot.Services.Hotkeys;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class HotkeyServiceTests
{
    private const string Target = "RocoPilot.Tests.NoGame";

    [TestMethod]
    public void MatchesOnlyTheGameAndExactModifiers()
    {
        var matcher = CreateMatcher(53, [17, 16]);
        Assert.IsFalse(matcher.ProcessKey(53, true, [17, 16], "notepad").Handled);
        Assert.IsFalse(matcher.ProcessKey(53, true, [17], Target).Handled);
        Assert.IsFalse(matcher.ProcessKey(53, true, [17, 16, 18], Target).Handled);
        Assert.IsFalse(matcher.ProcessKey(54, true, [17, 16], Target).Handled);
        var match = matcher.ProcessKey(53, true, [16, 17], Target.ToUpperInvariant());
        Assert.IsTrue(match.Handled);
        Assert.AreEqual(HotkeyAction.TakeScreenshot, match.Action);
    }

    [TestMethod]
    public void TriggersOnceAndConsumesTheMatchingReleaseAfterFocusChanges()
    {
        var matcher = CreateMatcher(54, []);
        Assert.AreEqual(HotkeyAction.TakeScreenshot, matcher.ProcessKey(54, true, [], Target).Action);
        var repeat = matcher.ProcessKey(54, true, [], "notepad");
        Assert.IsTrue(repeat.Handled);
        Assert.IsNull(repeat.Action);
        var release = matcher.ProcessKey(54, false, [], "notepad");
        Assert.IsTrue(release.Handled);
        Assert.IsNull(release.Action);
        Assert.IsFalse(matcher.ProcessKey(54, false, [], Target).Handled);
        Assert.AreEqual(HotkeyAction.TakeScreenshot, matcher.ProcessKey(54, true, [], Target).Action);
    }

    [TestMethod]
    public void RebindingReplacesTheOldKeyWithoutKeepingPressedState()
    {
        var matcher = CreateMatcher(54, []);
        matcher.ProcessKey(54, true, [], Target);
        matcher.Configure(new(Target, SettingsFor(113, [])));
        Assert.IsFalse(matcher.ProcessKey(54, true, [], Target).Handled);
        Assert.AreEqual(HotkeyAction.TakeScreenshot, matcher.ProcessKey(113, true, [], Target).Action);
        matcher.Configure(new(Target, new()));
        Assert.IsFalse(matcher.ProcessKey(113, true, [], Target).Handled);
    }

    [TestMethod]
    public async Task CancelledElevationKeepsTheSavedBindingAndRequestsOnlyTheHostRole()
    {
        var store = new ControlledSettingsStore();
        ProcessStartInfo? launch = null;
        using var service = CreateService(store, info =>
        {
            launch = info;
            throw new Win32Exception(1223);
        });
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.SetBindingAsync(HotkeyAction.TakeScreenshot, HotkeyBinding.Create([], 54)));
        StringAssert.Contains(failure.Message, "绑定已保存");
        StringAssert.Contains(service.UnavailableReason!, "已取消管理员授权");
        Assert.AreEqual(54, service.Settings.GetBinding(HotkeyAction.TakeScreenshot)!.Key);
        Assert.AreEqual(54, store.ReadSaved<HotkeySettings>(SettingsKeys.HotkeySettings)!.Bindings.Single().Binding!.Key);
        Assert.AreEqual("runas", launch!.Verb);
        Assert.AreEqual(ProcessWindowStyle.Hidden, launch.WindowStyle);
        Assert.AreEqual(HotkeyHost.Argument, launch.ArgumentList[0]);
        Assert.AreEqual(Environment.ProcessId.ToString(), launch.ArgumentList[2]);
    }

    [TestMethod]
    public async Task StartupCancellationDoesNotReplaceExistingBindingsWithDefaults()
    {
        var store = new ControlledSettingsStore();
        store.Seed(SettingsKeys.HotkeySettings, SettingsFor(54, []));
        using var service = CreateService(store, _ => throw new Win32Exception(1223));
        await service.LoadSettingsAsync();
        Assert.AreEqual(54, service.Settings.Bindings.Single().Binding!.Key);
        Assert.IsNotNull(service.UnavailableReason);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task NoBindingsDoesNotLaunchOrRequestElevation()
    {
        using var service = CreateService(new ControlledSettingsStore(), _ =>
            throw new AssertFailedException("没有绑定时不应启动辅助进程。"));
        await service.LoadSettingsAsync();
        Assert.IsNull(service.UnavailableReason);
    }

    [TestMethod]
    public async Task FailedSaveDoesNotApplyAnUnpersistedBindingOrLaunchTheHost()
    {
        var store = new ControlledSettingsStore { BeforeSave = _ => throw new IOException("写入失败") };
        using var service = CreateService(store, _ => throw new AssertFailedException("不应启动监听。"));
        await Assert.ThrowsExactlyAsync<IOException>(() => service.SetBindingAsync(
            HotkeyAction.TakeScreenshot, HotkeyBinding.Create([], 54)));
        Assert.AreEqual(0, service.Settings.Bindings.Count);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task BindingUpdatesReuseOneHostAndClearingAllBindingsExitsIt()
    {
        Process? host = null;
        var starts = 0;
        using var service = CreateService(new ControlledSettingsStore(), info =>
        {
            starts++;
            host = StartHost(info.ArgumentList);
            return Process.GetProcessById(host.Id);
        });
        try
        {
            await service.SetBindingAsync(HotkeyAction.TakeScreenshot, HotkeyBinding.Create([], 54));
            Assert.AreEqual(IntPtr.Zero, host!.MainWindowHandle);
            await service.SetBindingAsync(HotkeyAction.ToggleInfoOverlay, HotkeyBinding.Create([17], 53));
            Assert.AreEqual(1, starts);
            Assert.IsNull(service.UnavailableReason);
            Assert.AreEqual(2, service.Settings.Bindings.Count);
            var replaced = await service.SetBindingAsync(HotkeyAction.ToggleInfoOverlay, HotkeyBinding.Create([], 54));
            Assert.AreEqual(HotkeyAction.TakeScreenshot, replaced.ReplacedAction);
            Assert.AreEqual(1, service.Settings.Bindings.Count);
            await service.SetBindingAsync(HotkeyAction.ToggleInfoOverlay, null);
            await WaitForHostExitAsync(host!);
            Assert.AreEqual(0, host!.ExitCode);
            Assert.AreEqual(1, starts);
        }
        finally
        {
            service.Dispose();
            StopTestHost(host);
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task DisposingTheMainServiceExitsTheHostAndKeepsBindings()
    {
        Process? host = null;
        var store = new ControlledSettingsStore();
        using var service = CreateService(store, info =>
        {
            host = StartHost(info.ArgumentList);
            return Process.GetProcessById(host.Id);
        });
        try
        {
            await service.SetBindingAsync(HotkeyAction.TakeScreenshot, HotkeyBinding.Create([], 54));
            service.Dispose();
            await WaitForHostExitAsync(host!);
            Assert.AreEqual(0, host!.ExitCode);
            Assert.AreEqual(54, store.ReadSaved<HotkeySettings>(SettingsKeys.HotkeySettings)!.Bindings.Single().Binding!.Key);
        }
        finally { StopTestHost(host); }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task UnexpectedHostExitReportsFailureAndRebindingRestartsIt()
    {
        Process? host = null;
        var starts = 0;
        using var service = CreateService(new ControlledSettingsStore(), info =>
        {
            starts++;
            host = StartHost(info.ArgumentList);
            return Process.GetProcessById(host.Id);
        });
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SettingsChanged += (_, _) =>
        {
            if (service.UnavailableReason is not null) disconnected.TrySetResult();
        };
        try
        {
            await service.SetBindingAsync(HotkeyAction.TakeScreenshot, HotkeyBinding.Create([], 54));
            host!.Kill();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(54, service.Settings.Bindings.Single().Binding!.Key);
            await service.SetBindingAsync(HotkeyAction.TakeScreenshot, HotkeyBinding.Create([], 54));
            Assert.AreEqual(2, starts);
            Assert.IsNull(service.UnavailableReason);
            service.Dispose();
            await WaitForHostExitAsync(host!);
            Assert.AreEqual(0, host!.ExitCode);
        }
        finally
        {
            service.Dispose();
            StopTestHost(host);
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task HostRejectsAPipeWhoseServerDoesNotMatchItsParent()
    {
        var name = $"RocoPilot.Test.{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var host = StartHost([HotkeyHost.Argument, name, "0"]);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token);
            await host.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(1, host.ExitCode);
        }
        finally { StopTestHost(host); }
    }

    private static HotkeyMatcher CreateMatcher(int key, int[] modifiers)
    {
        var matcher = new HotkeyMatcher();
        matcher.Configure(new(Target, SettingsFor(key, modifiers)));
        return matcher;
    }

    private static HotkeySettings SettingsFor(int key, int[] modifiers) => new()
    {
        Bindings = [new() { Action = HotkeyAction.TakeScreenshot, Binding = HotkeyBinding.Create(modifiers, key) }]
    };

    private static HotkeyService CreateService(ControlledSettingsStore store, Func<ProcessStartInfo, Process?> launch) =>
        new(store, new GameWindowStub(), NullLogger<HotkeyService>.Instance, launch);

    private static Process StartHost(IEnumerable<string> arguments)
    {
        // 测试不触发 UAC；同一个生产辅助进程入口验证 IPC 和生命周期，提权边界另做现场验证。
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RocoPilot.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    private static async Task WaitForHostExitAsync(Process host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.WaitForExitAsync(timeout.Token);
    }

    private static void StopTestHost(Process? host)
    {
        if (host is not null && !host.HasExited) host.Kill();
    }

    private sealed class GameWindowStub : IGameWindowService
    {
        public string TargetProcessName => Target + ".exe";
        public CaptureTargetWindow? FindGameWindow() => null;
        public bool TryBringGameWindowToForeground(CaptureTargetWindow window) => false;
    }
}
