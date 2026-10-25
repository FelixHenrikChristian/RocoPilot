using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Statistics;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsUidCoordinatorTests
{
    [TestMethod]
    [DataRow("stop")]
    [DataRow("confirm")]
    [DataRow("new-request")]
    public async Task OldRetryCannotRestoreOrReplaceConfirmation(string action)
    {
        var fixture = new UidFixture();
        await fixture.PrepareAsync();
        var pendingDetection = new TaskCompletionSource<StatisticsUidDetectionResult>();
        fixture.Detection.Detect = () => pendingDetection.Task;
        var retry = fixture.Coordinator.RetryDetectionAsync();
        fixture.Coordinator.Clear();
        if (action == "confirm") await fixture.Coordinator.ConfirmUidAsync("100");
        if (action == "new-request")
        {
            fixture.Detection.Detect = () => Task.FromResult(StatisticsUidDetectionResult.Failed("new request"));
            await fixture.PrepareAsync();
        }
        var current = fixture.Coordinator.PendingConfirmation;
        var notice = fixture.Notice.Current;

        pendingDetection.SetResult(StatisticsUidDetectionResult.Detected("200"));
        await retry;

        Assert.AreSame(current, fixture.Coordinator.PendingConfirmation);
        Assert.AreSame(notice, fixture.Notice.Current);
        if (action == "confirm") Assert.AreEqual("100", fixture.Statistics.ActiveAccountUid);
    }

    [TestMethod]
    public async Task ConfirmationInvalidatesPendingRetryWithoutStoppingTask()
    {
        var fixture = new UidFixture();
        await fixture.PrepareAsync();
        var pendingDetection = new TaskCompletionSource<StatisticsUidDetectionResult>();
        fixture.Detection.Detect = () => pendingDetection.Task;
        var retry = fixture.Coordinator.RetryDetectionAsync();
        await fixture.Coordinator.ConfirmUidAsync("100");

        pendingDetection.SetResult(StatisticsUidDetectionResult.Detected("200"));
        await retry;

        Assert.IsNull(fixture.Coordinator.PendingConfirmation);
        Assert.IsNull(fixture.Notice.Current);
        Assert.AreEqual("100", fixture.Statistics.ActiveAccountUid);
    }

    [TestMethod]
    public async Task OldPreparationCannotSelectAccountAfterClear()
    {
        var fixture = new UidFixture();
        await fixture.Statistics.AddAccountAsync("100");
        fixture.Statistics.SetSelectedAccountUid("100");
        var pendingDetection = new TaskCompletionSource<StatisticsUidDetectionResult>();
        fixture.Detection.Detect = () => pendingDetection.Task;
        var preparing = fixture.PrepareAsync();
        Assert.IsTrue(fixture.Statistics.IsActiveAccountSelectionRequired);
        fixture.Coordinator.Clear();

        pendingDetection.SetResult(StatisticsUidDetectionResult.Detected("100"));
        await preparing;

        Assert.IsTrue(fixture.Statistics.IsActiveAccountSelectionRequired);
        Assert.IsNull(fixture.Statistics.ActiveAccountUid);
        Assert.IsNull(fixture.Coordinator.PendingConfirmation);
        Assert.IsNull(fixture.Notice.Current);
    }

    [TestMethod]
    public async Task LatestRetryWinsAndPresentationDoesNotInvalidateIt()
    {
        var fixture = new UidFixture();
        await fixture.PrepareAsync();
        var firstDetection = new TaskCompletionSource<StatisticsUidDetectionResult>();
        var secondDetection = new TaskCompletionSource<StatisticsUidDetectionResult>();
        fixture.Detection.Detect = () => firstDetection.Task;
        var first = fixture.Coordinator.RetryDetectionAsync();
        fixture.Detection.Detect = () => secondDetection.Task;
        var second = fixture.Coordinator.RetryDetectionAsync();
        fixture.Coordinator.MarkPendingConfirmationPresented();

        secondDetection.SetResult(StatisticsUidDetectionResult.Detected("200"));
        await second;
        firstDetection.SetResult(StatisticsUidDetectionResult.Detected("100"));
        await first;

        Assert.AreEqual("200", fixture.Coordinator.PendingConfirmation?.SuggestedUid);
        Assert.IsTrue(fixture.Coordinator.PendingConfirmation!.WasPresented);
        StringAssert.Contains(fixture.Notice.Current!.Message, "200");
    }

    [TestMethod]
    public async Task PreparationBlocksCountingBeforeLoadingAndDoesNotConfirmFallbackAccount()
    {
        var fixture = new UidFixture();
        fixture.Store.Seed(SettingsKeys.StatisticsData, new StatisticsDocument
        {
            Accounts = [new() { Uid = "100" }]
        });
        fixture.Statistics.SetActiveAccountUid("200");
        using var pause = new AsyncPause();
        fixture.Store.BeforeRead = pause.PauseAsync;
        var preparing = fixture.PrepareAsync();
        await pause.WaitUntilEnteredAsync();
        Assert.IsTrue(fixture.Statistics.IsActiveAccountSelectionRequired);
        Assert.IsNull(fixture.Statistics.ActiveAccountUid);
        pause.Dispose();
        await preparing;

        Assert.IsTrue(fixture.Statistics.IsActiveAccountSelectionRequired);
        Assert.IsNull(fixture.Statistics.ActiveAccountUid);
        Assert.AreEqual("100", fixture.Coordinator.PendingConfirmation?.SuggestedUid);
    }

    [TestMethod]
    public async Task FailedLoadStillLeavesConfirmationAvailable()
    {
        var fixture = new UidFixture();
        fixture.Store.BeforeRead = _ => throw new IOException("read failed");

        await fixture.Coordinator.PrepareEnabledStatisticsAsync(new());

        Assert.IsTrue(fixture.Statistics.IsActiveAccountSelectionRequired);
        Assert.IsNotNull(fixture.Coordinator.PendingConfirmation);
        Assert.IsNotNull(fixture.Notice.Current);
    }

    [TestMethod]
    public async Task StoppedConfirmationCannotSelectAccountAfterSaveCompletes()
    {
        var fixture = new UidFixture();
        await fixture.PrepareAsync();
        using var pause = new AsyncPause();
        fixture.Store.BeforeSave = pause.PauseAsync;
        var confirming = fixture.Coordinator.ConfirmUidAsync("100");
        await pause.WaitUntilEnteredAsync();
        fixture.Coordinator.Clear();
        pause.Dispose();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => confirming);

        Assert.IsTrue(fixture.Statistics.IsActiveAccountSelectionRequired);
        Assert.IsNull(fixture.Statistics.ActiveAccountUid);
        Assert.IsNull(fixture.Coordinator.PendingConfirmation);
    }

    private sealed class UidFixture
    {
        public ControlledSettingsStore Store { get; } = new();
        public StatisticsService Statistics { get; }
        public DetectionStub Detection { get; } = new();
        public NoticeStub Notice { get; } = new();
        public StatisticsUidCoordinatorService Coordinator { get; }

        public UidFixture()
        {
            Statistics = new(Store, NullLogger<StatisticsService>.Instance);
            Coordinator = new(Detection, Statistics, Notice, NullLogger<StatisticsUidCoordinatorService>.Instance);
        }

        public async Task PrepareAsync()
        {
            var preparation = await Coordinator.PrepareStartAsync(
                new() { EncounterStatisticsEnabled = true }, CancellationToken.None);
            Coordinator.CompleteStart(preparation);
        }
    }

    internal sealed class DetectionStub : IStatisticsUidDetectionService
    {
        public int Calls;
        public Func<Task<StatisticsUidDetectionResult>> Detect = () =>
            Task.FromResult(StatisticsUidDetectionResult.Failed("unavailable"));
        public Task<StatisticsUidDetectionResult> DetectAsync(CaptureMethod capture,
            TextRecognitionMethod ocr, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Detect();
        }
    }

    internal sealed class NoticeStub : IInfoOverlayNotificationService
    {
        public InfoOverlayNotice? Current;
        public void UpdateUidNotice(InfoOverlayNotice? notice) => Current = notice;
    }
}
