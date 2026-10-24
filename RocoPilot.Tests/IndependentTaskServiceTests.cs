using System.Collections.Concurrent;

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
    [DataRow(IndependentTaskKind.FlowerSeedChallenge, false)]
    [DataRow(IndependentTaskKind.FlowerSeedChallenge, true)]
    [DataRow(IndependentTaskKind.LegendaryChallenge, false)]
    [DataRow(IndependentTaskKind.LegendaryChallenge, true)]
    [DataRow(IndependentTaskKind.FlowerSeedScan, false)]
    [DataRow(IndependentTaskKind.FlowerSeedScan, true)]
    [DataRow(IndependentTaskKind.LegendaryScan, false)]
    [DataRow(IndependentTaskKind.LegendaryScan, true)]
    public async Task TaskResultsRespectNotificationSettingsAndSkipSuccessfulScans(IndependentTaskKind kind, bool fails)
    {
        foreach (var enabled in new[] { true, false })
        {
            var fixture = new Fixture();
            await fixture.Notifications.SetEnabledAsync(enabled);
            if (fails) fixture.Runner.Failure = "测试失败原因";
            await fixture.Service.LoadSettingsAsync();
            Assert.IsTrue((await fixture.Service.StartAsync(kind)).Success);

            await WaitStoppedAsync(fixture.Service);

            var shouldNotify = enabled && (kind is not (IndependentTaskKind.FlowerSeedScan or IndependentTaskKind.LegendaryScan) || fails);
            Assert.AreEqual(shouldNotify ? 1 : 0, fixture.Notifications.Payloads.Count);
            if (shouldNotify)
            {
                var notification = fixture.Notifications.Payloads.Single();
                var result = fixture.Runtime.Results.Single();
                Assert.AreEqual((result.Title, result.Description), notification);
                StringAssert.Contains(notification.Title, fails ? "已停止" : "完成");
                if (fails) Assert.AreEqual(fixture.Runner.Failure, notification.Message);
            }
            Assert.AreEqual(1, fixture.Runtime.Resumes);
            Assert.IsFalse(fixture.Service.IsRunning);
        }
    }

    [TestMethod]
    [DataRow(IndependentTaskKind.LegendaryChallenge)]
    [DataRow(IndependentTaskKind.FlowerSeedScan)]
    public async Task ManuallyStoppedTasksDoNotNotify(IndependentTaskKind kind)
    {
        var fixture = new Fixture();
        fixture.Runner.Hold = true;
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(kind)).Success);
        await (kind == IndependentTaskKind.FlowerSeedScan ? fixture.Runner.ScanStarted : fixture.Runner.ChallengeStarted)
            .Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Service.StopAsync();

        Assert.HasCount(0, fixture.Notifications.Payloads);
        Assert.HasCount(0, fixture.Runtime.Results);
        Assert.AreEqual(1, fixture.Runtime.Resumes);
    }

    [TestMethod]
    public async Task LegendaryUsesConfiguredTargetCountAndMarkPreference()
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        var settings = fixture.Service.Settings;
        Assert.AreEqual(3, settings.LegendaryChallengeRunCount);
        Assert.AreEqual(3, settings.LegendaryDifficulty);
        Assert.IsTrue(settings.UseLegendaryMarkWhenInsufficient);
        settings.LegendaryTargetName = "龙息帕尔";
        settings.LegendaryChallengeRunCount = 5;
        settings.LegendaryDifficulty = 2;
        settings.UseLegendaryMarkWhenInsufficient = false;
        fixture.Service.SetSettings(settings);

        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryChallenge)).Success);
        await fixture.Runner.ChallengeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitStoppedAsync(fixture.Service);

        Assert.AreEqual("龙息帕尔", fixture.Runner.Target!.Name);
        Assert.AreEqual(5, fixture.Runner.RunCount);
        Assert.AreEqual(2, fixture.Runner.Difficulty);
        Assert.AreEqual(2, fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!.LegendaryDifficulty);
        Assert.IsFalse(fixture.Runner.UseLegendaryMark);
        Assert.AreEqual(1, fixture.Runtime.Resumes);
        Assert.IsTrue(fixture.Runtime.Results.Any(result => result.Title == "传说精灵挑战完成，成功 5 次"));
    }

    [TestMethod]
    public async Task LegendaryScanAndSelectionAreTemporaryWhileOtherSettingsStillPersist()
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        CollectionAssert.AreEqual(new[] { "圣羽翼王", "龙息帕尔", "银月狼王" }, fixture.Service.LegendaryTargets.ToArray());
        var settings = fixture.Service.Settings;
        settings.LegendaryTargetName = "龙息帕尔";
        fixture.Service.SetSettings(settings);
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryScan)).Success);
        await WaitStoppedAsync(fixture.Service);

        CollectionAssert.AreEqual(fixture.Runner.LegendaryNames.ToArray(), fixture.Service.LegendaryTargets.ToArray());
        Assert.AreEqual(string.Empty, fixture.Service.Settings.LegendaryTargetName);
        Assert.IsFalse((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryChallenge)).Success);
        settings = fixture.Service.Settings;
        settings.LegendaryTargetName = "新传说精灵";
        settings.LegendaryDifficulty = 1;
        settings.LegendaryChallengeRunCount = 7;
        fixture.Service.SetSettings(settings);
        // 页面重新加载、再次扫描以及花种扫描的保存路径均不能持久化临时选择。
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryScan)).Success);
        await WaitStoppedAsync(fixture.Service);
        Assert.AreEqual("新传说精灵", fixture.Service.Settings.LegendaryTargetName);
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.FlowerSeedScan)).Success);
        await WaitStoppedAsync(fixture.Service);
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryChallenge)).Success);
        await WaitStoppedAsync(fixture.Service);
        Assert.AreEqual("新传说精灵", fixture.Runner.Target!.Name);

        var restarted = new IndependentTaskService(fixture.Runtime, fixture.Store, fixture.Runner,
            fixture.Notifications, fixture.Window, NullLogger<IndependentTaskService>.Instance);
        await restarted.LoadSettingsAsync();
        CollectionAssert.AreEqual(IndependentTaskSettings.LegendaryTargets.ToArray(), restarted.LegendaryTargets.ToArray());
        Assert.AreEqual("龙息帕尔", restarted.Settings.LegendaryTargetName);
        Assert.AreEqual(1, restarted.Settings.LegendaryDifficulty);
        Assert.AreEqual(7, restarted.Settings.LegendaryChallengeRunCount);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("failure")]
    [DataRow("cancel")]
    public async Task UnsuccessfulLegendaryRefreshPreservesPreviousScanAndSelection(string outcome)
    {
        var fixture = new Fixture();
        await fixture.Service.LoadSettingsAsync();
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryScan)).Success);
        await WaitStoppedAsync(fixture.Service);
        var original = fixture.Service.LegendaryTargets.ToArray();
        var settings = fixture.Service.Settings;
        settings.LegendaryTargetName = "新传说精灵";
        fixture.Service.SetSettings(settings);
        fixture.Runner.LegendaryNames = [];
        fixture.Runner.Failure = outcome == "failure" ? "识别失败" : null;
        fixture.Runner.Hold = outcome == "cancel";
        Assert.IsTrue((await fixture.Service.StartAsync(IndependentTaskKind.LegendaryScan)).Success);
        if (outcome == "cancel") await fixture.Service.StopAsync();
        else await WaitStoppedAsync(fixture.Service);
        CollectionAssert.AreEqual(original, fixture.Service.LegendaryTargets.ToArray());
        Assert.AreEqual("新传说精灵", fixture.Service.Settings.LegendaryTargetName);
        Assert.AreEqual("圣羽翼王", fixture.Store.ReadSaved<IndependentTaskSettings>(SettingsKeys.IndependentTaskSettings)!.LegendaryTargetName);
    }

    [TestMethod]
    public void DifficultySurvivesSettingsRoundTripAndOldSettingsDefaultToThree()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<IndependentTaskSettings>("{}")!;
        Assert.AreEqual(3, old.Normalize().LegendaryDifficulty);
        var settings = new IndependentTaskSettings { LegendaryDifficulty = 1 };
        var saved = System.Text.Json.JsonSerializer.Serialize(settings.Clone().Normalize());
        Assert.AreEqual(1, System.Text.Json.JsonSerializer.Deserialize<IndependentTaskSettings>(saved)!.LegendaryDifficulty);
        settings.LegendaryDifficulty = 4;
        Assert.AreEqual(3, settings.Normalize().LegendaryDifficulty);
    }

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
        Assert.AreEqual(0, fixture.Window.ForegroundCalls);
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
        Assert.AreEqual(0, fixture.Window.ForegroundCalls);
    }

    [TestMethod]
    [DataRow(IndependentTaskKind.FlowerSeedChallenge)]
    [DataRow(IndependentTaskKind.FlowerSeedScan)]
    public async Task StartingAnIndependentTaskBringsTheGameForwardOnceAfterRuntimeDrains(IndependentTaskKind kind)
    {
        var fixture = new Fixture(blockDrain: true);
        fixture.Runner.Hold = true;
        await fixture.Service.LoadSettingsAsync();
        fixture.Window.OnForeground = window =>
        {
            Assert.IsTrue(fixture.Runtime.IsSuspended && fixture.Runtime.Drained);
            Assert.AreSame(fixture.Runtime.CurrentState.TargetWindow, window);
        };
        try
        {
            Assert.IsTrue((await fixture.Service.StartAsync(kind)).Success);
            await fixture.Runtime.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, fixture.Window.ForegroundCalls);
            fixture.Runtime.DrainRelease.TrySetResult();
            await (kind == IndependentTaskKind.FlowerSeedScan ? fixture.Runner.ScanStarted : fixture.Runner.ChallengeStarted)
                .Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, fixture.Window.ForegroundCalls);
            Assert.IsFalse((await fixture.Service.StartAsync(kind)).Success);
            Assert.AreEqual(1, fixture.Window.ForegroundCalls);
        }
        finally
        {
            fixture.Runtime.DrainRelease.TrySetResult();
            await fixture.Service.StopAsync();
        }
        Assert.AreEqual(1, fixture.Window.ForegroundCalls);
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
        Assert.HasCount(1, fixture.Notifications.Payloads);
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
        Assert.AreEqual(("花种挑战已停止", fixture.Runner.Failure), fixture.Notifications.Payloads.Single());
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
        Assert.AreEqual(("扫描花种已停止", fixture.Runner.Failure), fixture.Runtime.Results.Single());
        Assert.HasCount(1, fixture.Notifications.Payloads);
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
        Assert.HasCount(0, fixture.Notifications.Payloads);
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
    [DataRow(IndependentTaskKind.FlowerSeedChallenge)]
    [DataRow(IndependentTaskKind.LegendaryChallenge)]
    public async Task ExistingTasksRemainExclusiveAndRunUntilStopped(IndependentTaskKind kind)
    {
        var fixture = new Fixture();
        fixture.Runner.Hold = true;
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
        public WindowStub Window { get; } = new();
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
            Service = new(Runtime, Store, Runner, Notifications, Window, NullLogger<IndependentTaskService>.Instance);
        }
    }

    private sealed class WindowStub : IGameWindowService
    {
        public int ForegroundCalls;
        public Action<CaptureTargetWindow>? OnForeground;
        public string TargetProcessName => "test-game";
        public CaptureTargetWindow? FindGameWindow() => throw new NotSupportedException();
        public bool TryBringGameWindowToForeground(CaptureTargetWindow window)
        {
            ForegroundCalls++;
            OnForeground?.Invoke(window);
            return true;
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

    private sealed class RunnerStub(RuntimeStub runtime) : IManualChallengeRunner
    {
        public IReadOnlyList<string> LegendaryNames = ["圣羽翼王", "银月狼王", "新传说精灵"];

        public async Task<IReadOnlyList<string>> ScanLegendaryAsync(RuntimeTaskState state,
            Action<IndependentTaskProgress> progress, CancellationToken cancellationToken)
        {
            await ScanAsync(state, progress, cancellationToken);
            return LegendaryNames;
        }

        public Task RunLegendaryAsync(RuntimeTaskState state, string targetName, int runCount,
            int difficulty, bool useLegendaryMark, AutoBattleSettings battleSettings,
            Action<IndependentTaskProgress> progress, CancellationToken cancellationToken)
        {
            UseLegendaryMark = useLegendaryMark;
            Difficulty = difficulty;
            return RunAsync(state, new(0, targetName), runCount, battleSettings, progress, cancellationToken);
        }

        public bool UseLegendaryMark;
        public int Difficulty;

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
        public ConcurrentQueue<(string Title, string Message)> Payloads { get; } = new();
        public bool ShowSucceeds = true;
        public string? LastError => null;
        public bool IsEnabled { get; private set; } = true;
        public bool IsSoundEnabled { get; private set; } = true;
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SetEnabledAsync(bool enabled) { IsEnabled = enabled; return Task.CompletedTask; }
        public Task SetSoundEnabledAsync(bool enabled) { IsSoundEnabled = enabled; return Task.CompletedTask; }
        public void Unregister() { }
        public Task<bool> ShowAsync(string title, string message, string? imagePath = null)
        {
            Payloads.Enqueue((title, message));
            return Task.FromResult(ShowSucceeds);
        }
    }
}
