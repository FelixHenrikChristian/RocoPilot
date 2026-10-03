using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Overlay;

namespace RocoPilot.Tests;

[TestClass]
public sealed class InfoOverlaySnapshotTests
{
    [TestMethod]
    public void LatestRecordTimeUsesCaptureTimeInsteadOfRecognitionRefreshTime()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 3, 14, 27, 18, TimeSpan.FromHours(8));
        var snapshot = new InfoOverlaySnapshot("技能选择",
            [new InfoOverlayCounter("小灵菇", 3, 0, capturedAt)], capturedAt.AddMinutes(3));

        Assert.AreEqual(capturedAt, snapshot.LatestRecordUpdatedAt);
        Assert.AreEqual(capturedAt,
            (snapshot with { UpdatedAt = capturedAt.AddHours(1), StatusText = "战斗结束" }).LatestRecordUpdatedAt);
    }

    [TestMethod]
    public void LatestRecordTimeFindsNewestRecordRegardlessOfOrderOrCount()
    {
        var latest = new DateTimeOffset(2026, 10, 3, 14, 27, 18, TimeSpan.FromHours(8));
        var snapshot = new InfoOverlaySnapshot("技能选择",
            [new InfoOverlayCounter("栗鼠", 99, 0, latest.AddMinutes(-5)),
             new InfoOverlayCounter("小灵菇", 1, 0, latest),
             new InfoOverlayCounter("稻草人", 12, 0, latest.AddMinutes(-2))], latest.AddMinutes(3));

        Assert.AreEqual(latest, snapshot.LatestRecordUpdatedAt);
    }

    [TestMethod]
    public void InitialSnapshotDoesNotCreateAFakeCaptureTime()
    {
        var startedAt = DateTimeOffset.Now;
        var snapshot = InfoOverlaySnapshot.CreateInitial(startedAt);

        Assert.AreEqual(0, snapshot.Counters.Count);
        Assert.IsNull(snapshot.LatestRecordUpdatedAt);
    }
}
