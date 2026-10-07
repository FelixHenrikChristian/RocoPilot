using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Xml.Linq;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class IndependentTaskServiceTests
{
    [TestMethod]
    public async Task ChallengeWithoutTargetDoesNotSuspendRuntime()
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        fixture.Service.SetSettings(new());

        var result = await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "目标花种");
        Assert.AreEqual(0, fixture.Runtime.Suspends);
        Assert.IsFalse(fixture.Service.IsRunning);
    }

    [TestMethod]
    public async Task ChallengeRequiresASelectedNumberFromTheCurrentScan()
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        fixture.Service.SetSettings(new()
        {
            FlowerSeedOptions = [new(1, "小皮球")],
            FlowerSeedTargetNumber = 2
        });

        var result = await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, fixture.Runtime.Suspends);
        Assert.IsFalse(fixture.Runner.ChallengeStarted.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ChallengeAcceptsAnOptionWithoutACatalogMatch()
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        fixture.Service.SetSettings(new()
        {
            FlowerSeedOptions = [new(1, "电球羊羊")],
            FlowerSeedTargetNumber = 1
        });

        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge)).Success);
        await WaitStoppedAsync(fixture.Service);

        Assert.AreEqual(new FlowerSeedOption(1, "电球羊羊"), fixture.Runner.Target);
        Assert.AreEqual(6, fixture.Runner.RunCount);
        Assert.AreEqual(("花种挑战完成，成功 6 次", "已退出并返回大世界"), fixture.Runtime.Results.Single());
        Assert.AreEqual(0, fixture.Notifications.Payloads.Count);
        Assert.AreEqual(1, fixture.Runtime.Resumes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedChallengeReportsResultNotifiesAndResumesRuntime(bool notificationAccepted)
    {
        var fixture = new Fixture();
        fixture.Runner.Failure = "连续 3 次失败：<友爱星飞> & 等待检查";
        fixture.Notifications.ShowSucceeds = notificationAccepted;
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge)).Success);

        await WaitStoppedAsync(fixture.Service);

        Assert.AreEqual(("花种挑战已停止", fixture.Runner.Failure), fixture.Runtime.Results.Single());
        var toast = XDocument.Parse(fixture.Notifications.Payloads.Single());
        CollectionAssert.AreEqual(new[] { "花种挑战已停止", fixture.Runner.Failure },
            toast.Descendants("text").Select(element => element.Value).ToArray());
        Assert.IsTrue(fixture.Runtime.Progress.Any(item => item.Name == "花种挑战"
            && item.Progress?.Stage == "任务失败" && item.Progress.Operation == fixture.Runner.Failure));
        Assert.AreEqual(1, fixture.Runtime.Resumes);
        Assert.IsFalse(fixture.Runtime.IsSuspended);
        Assert.IsFalse(fixture.Service.IsRunning);
        Assert.IsNull(fixture.Runtime.LastTaskName);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancelledChallengeDoesNotNotifyOrReportCompletionAndResumesRuntime(bool cancelWhileDraining)
    {
        var fixture = new Fixture(blockDrain: cancelWhileDraining);
        fixture.Runner.Hold = true;
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge)).Success);
        if (cancelWhileDraining)
            await fixture.Runtime.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else
            await fixture.Runner.ChallengeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Service.StopAsync();

        Assert.AreEqual(0, fixture.Notifications.Payloads.Count);
        Assert.AreEqual(0, fixture.Runtime.Results.Count);
        Assert.IsFalse(fixture.Runtime.Progress.Any(item => item.Progress?.Stage == "任务失败"));
        Assert.AreEqual(1, fixture.Runtime.Resumes);
        Assert.IsFalse(fixture.Runtime.IsSuspended);
        Assert.IsFalse(fixture.Service.IsRunning);
        Assert.IsNull(fixture.Runtime.LastTaskName);
        Assert.AreEqual(!cancelWhileDraining, fixture.Runner.ChallengeStarted.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ScanClearsSavedChoicesBeforeWaitingForRuntimeToDrain()
    {
        var fixture = new Fixture(blockDrain: true);
        await fixture.Service.LoadSettingsAsync();
        try
        {
            Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);
            await fixture.Runtime.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(0, fixture.Service.Settings.FlowerSeedOptions.Count);
            Assert.AreEqual(0, fixture.Service.Settings.FlowerSeedTargetNumber);
            AssertCleared(fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!);
            Assert.IsFalse(fixture.Runner.ScanStarted.Task.IsCompleted, "实时任务排空前不得截图或输入。");
            Assert.AreEqual(0, fixture.Runtime.Results.Count);

            fixture.Runtime.DrainRelease.TrySetResult();
            await fixture.Runner.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitStoppedAsync(fixture.Service);
            Assert.AreEqual(1, fixture.Runtime.Resumes);
        }
        finally
        {
            await fixture.Service.StopAsync();
        }
    }

    [TestMethod]
    public async Task CancelledScanKeepsPreviousChoicesClearedAndResumesRuntime()
    {
        var fixture = new Fixture();
        fixture.Runner.Hold = true;
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);
        await fixture.Runner.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Service.StopAsync();

        AssertCleared(fixture.Service.Settings);
        AssertCleared(fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!);
        Assert.AreEqual(0, fixture.Runtime.Results.Count);
        Assert.AreEqual(1, fixture.Runtime.Resumes);
        Assert.IsFalse(fixture.Service.IsRunning);
        Assert.IsNull(fixture.Runtime.LastTaskName);
    }

    [TestMethod]
    public async Task FailedScanKeepsChoicesClearedAndReportsFailure()
    {
        var fixture = new Fixture();
        fixture.Runner.Failure = "未识别到花种列表";
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);

        await WaitStoppedAsync(fixture.Service);

        AssertCleared(fixture.Service.Settings);
        AssertCleared(fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!);
        Assert.AreEqual(0, fixture.Runtime.Results.Count);
        Assert.IsTrue(fixture.Runtime.Progress.Any(item => item.Progress?.Stage == "任务失败"
            && item.Progress.Operation == "未识别到花种列表"));
        Assert.AreEqual(1, fixture.Runtime.Resumes);
    }

    [TestMethod]
    public async Task SuccessfulScanPreservesEveryNumberIncludingDuplicateAndUnmatchedNamesAndReplacesPreviousChoices()
    {
        var fixture = new Fixture();
        fixture.Runner.Options = [new(1, "小皮球"), new(2, "电球羊羊"), new(3, "小皮球"), new(4, "")];
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);

        await WaitStoppedAsync(fixture.Service);

        CollectionAssert.AreEqual(fixture.Runner.Options.ToArray(), fixture.Service.Settings.FlowerSeedOptions);
        Assert.AreEqual(0, fixture.Service.Settings.FlowerSeedTargetNumber);
        var saved = fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!;
        CollectionAssert.AreEqual(fixture.Service.Settings.FlowerSeedOptions, saved.FlowerSeedOptions);
        Assert.AreEqual(0, saved.FlowerSeedTargetNumber);
        Assert.AreEqual(4, saved.FlowerSeedOptions.Count);
        Assert.AreEqual(1, fixture.Runtime.Results.Count);
        Assert.AreEqual("扫描完成，共识别 4 个花种", fixture.Runtime.Results.Single().Title);
        Assert.AreEqual(1, fixture.Runtime.Resumes);
        Assert.IsFalse((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge)).Success);

        fixture.Runner.Options = [new(1, "怖哭菇")];
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);
        await WaitStoppedAsync(fixture.Service);
        var rescanned = fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!;
        CollectionAssert.AreEqual(new[] { new FlowerSeedOption(1, "怖哭菇") }, rescanned.FlowerSeedOptions);
        Assert.AreEqual(0, rescanned.FlowerSeedTargetNumber);
        Assert.AreEqual(1, rescanned.FlowerSeedOptions.Count);
        Assert.AreEqual("扫描完成，共识别 1 个花种", fixture.Runtime.Results.Last().Title);
        Assert.AreEqual(2, fixture.Runtime.Results.Count);
    }

    [TestMethod]
    public async Task PendingSettingsWriteCannotRestorePreviousScanChoices()
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        using var oldSave = new AsyncPause();
        fixture.Store.BeforeSave = _ => fixture.Store.SaveCount == 1 ? oldSave.PauseAsync("") : Task.CompletedTask;
        fixture.Service.SetSettings(fixture.Service.Settings);
        await oldSave.WaitUntilEnteredAsync();

        var start = fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan);
        Assert.IsFalse(start.IsCompleted, "新扫描必须等旧设置写入结束后再持久化清空。");
        oldSave.Dispose();
        Assert.IsTrue((await start.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        await WaitStoppedAsync(fixture.Service);

        var saved = fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!;
        CollectionAssert.AreEqual(new[] { new FlowerSeedOption(1, "小皮球") }, saved.FlowerSeedOptions);
        Assert.AreEqual(0, saved.FlowerSeedTargetNumber);
    }

    [TestMethod]
    public async Task ChallengeKeepsStartupTargetRunCountAndCurrentBattleSettingsWhileRuntimeDrains()
    {
        var fixture = new Fixture(blockDrain: true);
        fixture.Runner.Hold = true;
        await fixture.Service.LoadSettingsAsync();
        fixture.Service.SetSettings(new()
        {
            FlowerSeedOptions = [new(1, "小皮球"), new(2, "小皮球")],
            FlowerSeedTargetNumber = 2,
            FlowerSeedRunCount = 4
        });
        fixture.Runtime.BattleSettings.ReleaseSequence = [AutoBattleReleaseStep.CreateSkill("2")];
        fixture.Runtime.BattleSettings.FlowerSeedReleaseSequence = [AutoBattleReleaseStep.CreateSkill("4")];
        fixture.Runtime.BattleSettings.KeyboardHoldDurationMs = 250;
        try
        {
            Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedChallenge)).Success);
            await fixture.Runtime.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var settings = fixture.Service.Settings;
            settings.FlowerSeedTargetNumber = 1;
            settings.FlowerSeedRunCount = 9;
            fixture.Service.SetSettings(settings);
            fixture.Runtime.BattleSettings.ReleaseSequence[0].SkillKey = "1";
            fixture.Runtime.BattleSettings.FlowerSeedReleaseSequence[0].SkillKey = "3";
            fixture.Runtime.BattleSettings.KeyboardHoldDurationMs = 500;
            fixture.Runtime.BattleSettings.IsEnabled = true;
            Assert.IsFalse(fixture.Runner.ChallengeStarted.Task.IsCompleted);

            fixture.Runtime.DrainRelease.TrySetResult();
            await fixture.Runner.ChallengeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(new FlowerSeedOption(2, "小皮球"), fixture.Runner.Target);
            Assert.AreEqual(4, fixture.Runner.RunCount);
            Assert.IsNotNull(fixture.Runner.BattleSettings);
            Assert.AreEqual("2", fixture.Runner.BattleSettings.ReleaseSequence.Single().SkillKey);
            Assert.AreEqual("4", fixture.Runner.BattleSettings.FlowerSeedReleaseSequence.Single().SkillKey);
            Assert.AreEqual(250, fixture.Runner.BattleSettings.KeyboardHoldDurationMs);
            Assert.IsFalse(fixture.Runner.BattleSettings.IsEnabled);
            Assert.AreNotSame(fixture.Runtime.CurrentState.Options.AutoBattleSettings, fixture.Runner.BattleSettings);
            Assert.IsTrue(fixture.Runtime.Progress.Any(item => item.Name == "花种挑战"
                && item.Progress?.Stage == "魔法师手册"));
        }
        finally
        {
            await fixture.Service.StopAsync();
        }

        Assert.AreEqual(1, fixture.Runtime.Resumes);
    }

    [TestMethod]
    public async Task IndependentTaskDoesNotResumeAnExistingExternalSuspension()
    {
        var fixture = new Fixture(suspended: true);
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);

        await WaitStoppedAsync(fixture.Service);

        Assert.AreEqual(1, fixture.Runtime.Suspends);
        Assert.AreEqual(0, fixture.Runtime.Resumes);
        Assert.IsTrue(fixture.Runtime.IsSuspended);
    }

    [TestMethod]
    [DataRow(IndependentTaskKind.BossBattle)]
    [DataRow(IndependentTaskKind.LegendaryChallenge)]
    public async Task ExistingTasksRemainExclusiveAndRunUntilStopped(IndependentTaskKind kind)
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(kind)).Success);
        await fixture.Runtime.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(fixture.Service.IsRunning);
        Assert.IsFalse((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);
        CollectionAssert.AreEqual(new[] { new FlowerSeedOption(1, "友爱星飞"), new FlowerSeedOption(2, "旧花种") },
            fixture.Service.Settings.FlowerSeedOptions);
        Assert.IsFalse(fixture.Runner.ScanStarted.Task.IsCompleted);

        await fixture.Service.StopAsync();

        Assert.AreEqual(1, fixture.Runtime.Resumes);
        Assert.IsFalse(fixture.Service.IsRunning);
    }

    private static void AssertCleared(IndependentTaskSettings settings)
    {
        Assert.AreEqual(0, settings.FlowerSeedOptions.Count);
        Assert.AreEqual(0, settings.FlowerSeedTargetNumber);
    }

    private static async Task WaitStoppedAsync(IndependentTaskService service)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? sender, EventArgs args)
        {
            if (!service.IsRunning) stopped.TrySetResult();
        }

        service.StateChanged += OnStateChanged;
        try
        {
            if (!service.IsRunning) stopped.TrySetResult();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.StateChanged -= OnStateChanged;
        }
    }

    private sealed class Fixture
    {
        public RuntimeStub Runtime { get; }
        public RunnerStub Runner { get; }
        public ControlledSettingsStore Store { get; } = new();
        public NotificationStub Notifications { get; } = new();
        public IndependentTaskService Service { get; }

        public Fixture(bool blockDrain = false, bool suspended = false)
        {
            Runtime = new(blockDrain, suspended);
            Runner = new(Runtime);
            Store.Seed(SettingsKeys.IndependentTaskSettings, new IndependentTaskSettings
            {
                FlowerSeedOptions = [new(1, "友爱星飞"), new(2, "旧花种")],
                FlowerSeedTargetNumber = 1
            });
            Service = new(Runtime, Store, Runner, Notifications, NullLogger<IndependentTaskService>.Instance);
        }
    }

    private sealed class RuntimeStub : IRuntimeSessionControl
    {
        public bool IsRunning => true;
        public bool IsSuspended { get; private set; }
        public AutoBattleSettings BattleSettings { get; } = new();
        public AutoBattleSettings AutoBattleSettings => BattleSettings.Clone();
        public RuntimeTaskState CurrentState { get; } = new(new CaptureTargetWindow { Hwnd = 1 },
            new RecognitionRegionConfig(), new RuntimeTaskStartOptions(), DateTimeOffset.Now);
        public int Suspends;
        public int Resumes;
        public bool Drained;
        public string? LastTaskName;
        public ConcurrentQueue<(string? Name, IndependentTaskProgress? Progress)> Progress = new();
        public ConcurrentQueue<(string Title, string Description)> Results = new();
        public TaskCompletionSource DrainStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DrainRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeStub(bool blockDrain, bool suspended)
        {
            IsSuspended = suspended;
            if (!blockDrain) DrainRelease.TrySetResult();
        }

        public void Suspend(string reason) => IsSuspended = true;
        public async Task SuspendAsync(string reason, CancellationToken cancellationToken = default)
        {
            Suspends++;
            Suspend(reason);
            DrainStarted.TrySetResult();
            await DrainRelease.Task.WaitAsync(cancellationToken);
            Drained = true;
        }

        public void Resume() { Resumes++; IsSuspended = false; }
        public void ShowIndependentTaskResult(string title, string description) => Results.Enqueue((title, description));
        public void UpdateIndependentTaskStatus(string? taskName, IndependentTaskProgress? progress = null)
        {
            LastTaskName = taskName;
            Progress.Enqueue((taskName, progress));
        }
    }

    private sealed class RunnerStub(RuntimeStub runtime) : IFlowerSeedChallengeRunner
    {
        public IReadOnlyList<FlowerSeedOption> Options = [new(1, "小皮球")];
        public bool Hold;
        public string? Failure;
        public FlowerSeedOption? Target;
        public int RunCount;
        public AutoBattleSettings? BattleSettings;
        public TaskCompletionSource ScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChallengeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(RuntimeTaskState state, FlowerSeedOption target,
            int runCount, AutoBattleSettings battleSettings,
            Action<IndependentTaskProgress> progress, CancellationToken cancellationToken)
        {
            Assert.IsTrue(runtime.IsSuspended && runtime.Drained);
            Assert.AreSame(runtime.CurrentState, state);
            Target = target;
            RunCount = runCount;
            BattleSettings = battleSettings;
            progress(new("魔法师手册", "点击挑战页", "学院作业"));
            ChallengeStarted.TrySetResult();
            if (Failure is not null) throw new InvalidOperationException(Failure);
            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(RuntimeTaskState state,
            Action<IndependentTaskProgress> progress, CancellationToken cancellationToken)
        {
            Assert.IsTrue(runtime.IsSuspended && runtime.Drained);
            Assert.AreSame(runtime.CurrentState, state);
            progress(new("扫描花种", "向下滚动", "小皮球"));
            ScanStarted.TrySetResult();
            if (Failure is not null) throw new InvalidOperationException(Failure);
            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Options;
        }
    }

    private sealed class NotificationStub : IAppNotificationService
    {
        public ConcurrentQueue<string> Payloads { get; } = new();
        public bool ShowSucceeds = true;
        public void Initialize() { }
        public void Unregister() { }
        public NameValueCollection ParseArguments(string arguments) => new();
        public bool Show(string payload)
        {
            Payloads.Enqueue(payload);
            return ShowSucceeds;
        }
    }
}
