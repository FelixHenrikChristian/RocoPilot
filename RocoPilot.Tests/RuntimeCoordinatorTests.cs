using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Statistics;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services;
using RocoPilot.Services.Statistics;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RuntimeCoordinatorTests
{
    [TestMethod]
    public async Task StopsIndependentTaskBeforeRuntimeEvenWhenIndependentStopFails()
    {
        var calls = new List<string>();
        var runtime = new RuntimeStub(calls);
        var tasks = new IndependentStub(calls) { FailStop = true };
        var coordinator = new RuntimeCoordinator(runtime, tasks, CreateUidCoordinator(new DetectionStub()));
        try { await coordinator.StopAsync(); Assert.Fail("应报告独立任务停止失败。"); }
        catch (InvalidOperationException) { }
        CollectionAssert.AreEqual(new[] { "independent-stop", "runtime-stop" }, calls);
    }

    [TestMethod]
    public async Task PreparesUidOnceAndClearsPendingConfirmationOnStop()
    {
        var calls = new List<string>();
        var detection = new DetectionStub();
        var uid = CreateUidCoordinator(detection);
        var coordinator = new RuntimeCoordinator(new RuntimeStub(calls), new IndependentStub(calls), uid);
        var options = new RuntimeTaskStartOptions { EncounterStatisticsEnabled = true };
        Assert.IsTrue((await coordinator.StartAsync(options)).Success);
        Assert.IsNotNull(uid.PendingConfirmation);
        Assert.IsTrue((await coordinator.StartAsync(options)).Success);
        Assert.AreEqual(1, detection.Calls);
        await coordinator.StopAsync();
        Assert.IsNull(uid.PendingConfirmation);
    }

    [TestMethod]
    public async Task StopDuringUidPreparationWaitsAndThenStopsTheStartedRuntime()
    {
        var calls = new List<string>();
        var detection = new DetectionStub { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new RuntimeCoordinator(new RuntimeStub(calls), new IndependentStub(calls), CreateUidCoordinator(detection));
        var starting = coordinator.StartAsync(new RuntimeTaskStartOptions { EncounterStatisticsEnabled = true });
        await detection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = coordinator.StopAsync();
        Assert.IsFalse(stopping.IsCompleted);
        detection.Completion.SetResult(StatisticsUidDetectionResult.Failed("unavailable"));
        await Task.WhenAll(starting, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { "runtime-start", "independent-stop", "runtime-stop" }, calls);
        Assert.IsFalse(coordinator.IsRunning);
    }

    private static StatisticsUidCoordinatorService CreateUidCoordinator(DetectionStub detection) => new(
        detection, new StatisticsService(new MemorySettings(), NullLogger<StatisticsService>.Instance),
        new NoticeStub(), NullLogger<StatisticsUidCoordinatorService>.Instance);

    private sealed class DetectionStub : IStatisticsUidDetectionService
    {
        public int Calls;
        public TaskCompletionSource<StatisticsUidDetectionResult>? Completion;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StatisticsUidDetectionResult> DetectAsync(CaptureMethod capture, TextRecognitionMethod ocr, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            return Completion?.Task ?? Task.FromResult(StatisticsUidDetectionResult.Failed("unavailable"));
        }
    }

    private sealed class NoticeStub : IInfoOverlayNotificationService
    {
        public void UpdateUidNotice(InfoOverlayNotice? notice) { }
    }

    private sealed class MemorySettings : ILocalSettingsService
    {
        public Task<T?> ReadSettingAsync<T>(string key) => Task.FromResult(default(T));
        public Task SaveSettingAsync<T>(string key, T value) => Task.CompletedTask;
        public Task ResetAllAsync() => Task.CompletedTask;
    }

    private sealed class IndependentStub(List<string> calls) : IIndependentTaskService
    {
        public bool FailStop;
        public event EventHandler? StateChanged { add { } remove { } }
        public bool IsRunning => false;
        public IndependentTaskKind? RunningTaskKind => null;
        public IndependentTaskSettings Settings => IndependentTaskSettings.CreateDefault();
        public Task LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetSettings(IndependentTaskSettings settings) { }
        public Task<IndependentTaskStartResult> StartAsync(IndependentTaskKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync()
        {
            calls.Add("independent-stop");
            if (FailStop) throw new InvalidOperationException("stop failure");
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeStub(List<string> calls) : IRuntimeTaskService
    {
        public event EventHandler? SettingsChanged { add { } remove { } }
        public bool IsRunning => CurrentState is not null;
        public bool IsSuspended => false;
        public RuntimeTaskState? CurrentState { get; private set; }
        public bool EncounterStatisticsEnabled => true;
        public AutoBattleSettings AutoBattleSettings => AutoBattleSettings.CreateDefault();
        public RuntimeRecognitionSettings RuntimeRecognitionSettings => RuntimeRecognitionSettings.CreateDefault();
        public Task<RuntimeTaskStartResult> StartAsync(RuntimeTaskStartOptions options, CancellationToken cancellationToken = default)
        {
            if (CurrentState is null)
            {
                calls.Add("runtime-start");
                CurrentState = new(new CaptureTargetWindow(), new RecognitionRegionConfig(), options, DateTimeOffset.Now);
            }
            return Task.FromResult(RuntimeTaskStartResult.Started(CurrentState));
        }
        public Task StopAsync() { calls.Add("runtime-stop"); CurrentState = null; return Task.CompletedTask; }
        public Task LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetEncounterStatisticsEnabled(bool isEnabled) { }
        public void SetRecognitionOverlayEnabled(bool isEnabled) { }
        public void SetInfoOverlayEnabled(bool isEnabled) { }
        public void SetInfoOverlayLocked(bool isLocked) { }
        public void SetAutoBattleSettings(AutoBattleSettings settings) { }
        public void SetRuntimeRecognitionSettings(RuntimeRecognitionSettings settings) { }
        public void Suspend(string reason) { }
        public void Resume() { }
    }
}
