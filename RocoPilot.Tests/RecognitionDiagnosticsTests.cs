using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.Logging;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RecognitionDiagnosticsTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(3);

    [TestMethod]
    public void EveryCallIsTracedButOnlyChangesAreLoggedAsDebug()
    {
        var (diagnostics, log, _) = Create();

        for (var index = 0; index < 3; index++)
        {
            diagnostics.ReportMatch("1920x1080/battle-button-skill.png", "battle-skill-button", 0.412, 0.88, 0, Fast);
        }

        diagnostics.ReportMatch("1920x1080/battle-button-skill.png", "battle-skill-button", 0.973, 0.88, 1, Fast);
        diagnostics.ReportMatch("1920x1080/battle-button-skill.png", "battle-skill-button", 0.971, 0.88, 1, Fast);

        Assert.AreEqual(5, log.Count(LogLevel.Trace));
        CollectionAssert.AreEqual(
            new[]
            {
                "匹配变化 battle-button-skill@battle-skill-button：未命中，分数 0.412，阈值 0.88",
                "匹配变化 battle-button-skill@battle-skill-button：命中，分数 0.973，阈值 0.88"
            },
            log.Messages(LogLevel.Debug));
        Assert.AreEqual(
            "匹配 battle-button-skill@battle-skill-button：命中，分数 0.971，阈值 0.88，耗时 3.0 ms",
            log.Messages(LogLevel.Trace)[^1]);
    }

    [TestMethod]
    public void CountOfMatchesIsPartOfTheResult()
    {
        var (diagnostics, log, _) = Create();

        diagnostics.ReportMatch("1920x1080/magic-point.png", "magic-point", 0.99, 0.96, 6, Fast);
        diagnostics.ReportMatch("1920x1080/magic-point.png", "magic-point", 0.99, 0.96, 6, Fast);
        diagnostics.ReportMatch("1920x1080/magic-point.png", "magic-point", 0.98, 0.96, 5, Fast);

        CollectionAssert.AreEqual(
            new[]
            {
                "匹配变化 magic-point：命中 6 处，分数 0.990，阈值 0.96",
                "匹配变化 magic-point：命中 5 处，分数 0.980，阈值 0.96"
            },
            log.Messages(LogLevel.Debug));
    }

    [TestMethod]
    public void OcrTextIsLoggedWhenItChangesIgnoringLayoutWhitespace()
    {
        var (diagnostics, log, _) = Create();

        diagnostics.ReportText("battle-enemy-name", "", Fast);
        diagnostics.ReportText("battle-enemy-name", "  ", Fast);
        diagnostics.ReportText("battle-enemy-name", "栗鼠", Fast);
        diagnostics.ReportText("battle-enemy-name", " 栗鼠\r\n", Fast);
        diagnostics.ReportText("battle-bloodline-tip", "野生的栗鼠\r\n带有奇异血脉", Fast);

        CollectionAssert.AreEqual(
            new[]
            {
                "OCR 变化 battle-enemy-name：<empty>",
                "OCR 变化 battle-enemy-name：栗鼠",
                "OCR 变化 battle-bloodline-tip：野生的栗鼠 带有奇异血脉"
            },
            log.Messages(LogLevel.Debug));
        Assert.AreEqual(5, log.Count(LogLevel.Trace));
    }

    [TestMethod]
    public void FlickeringResultIsRateLimitedAndReportsHowManyChangesWereSkipped()
    {
        var (diagnostics, log, time) = Create();

        // 分数在阈值附近抖动：每 250 ms 变一次，共 20 次。
        for (var index = 0; index < 20; index++)
        {
            diagnostics.ReportMatch("battle-chat.png", null, 0.96, 0.96, index % 2, Fast);
            time.Advance(TimeSpan.FromMilliseconds(250));
        }

        // 先连记 4 条，之后 5 秒放行一条。
        Assert.AreEqual(4, log.Count(LogLevel.Debug));

        time.Advance(TimeSpan.FromSeconds(5));
        diagnostics.ReportMatch("battle-chat.png", null, 0.96, 0.96, 0, Fast);

        Assert.AreEqual(5, log.Count(LogLevel.Debug));
        Assert.AreEqual(
            "匹配变化 battle-chat：未命中，分数 0.960，阈值 0.96，此前另有 16 次变化未逐条记录",
            log.Messages(LogLevel.Debug)[^1]);
    }

    [TestMethod]
    public void WritesOneSummaryPerTargetWhenTheIntervalPasses()
    {
        var (diagnostics, log, time) = Create();
        diagnostics.ReportMatch("battle-chat.png", null, 0.50, 0.96, 0, TimeSpan.FromMilliseconds(2));
        diagnostics.ReportMatch("battle-chat.png", null, 0.98, 0.96, 1, TimeSpan.FromMilliseconds(4));
        diagnostics.ReportText("battle-enemy-name", "栗鼠", TimeSpan.FromMilliseconds(40));
        diagnostics.ReportText("battle-enemy-name", "", TimeSpan.FromMilliseconds(60));
        log.Clear();

        time.Advance(RecognitionDiagnostics.SummaryInterval);
        diagnostics.ReportMatch("battle-chat.png", null, 0.97, 0.96, 1, TimeSpan.FromMilliseconds(6));

        CollectionAssert.AreEqual(
            new[]
            {
                "匹配汇总 battle-chat：30 秒内 3 次，命中 2 次，变化 2 次，分数 0.500~0.980，阈值 0.96，平均 4.0 ms，最慢 6.0 ms",
                "OCR 汇总 battle-enemy-name：30 秒内 2 次，非空 1 次，变化 2 次，平均 50 ms，最慢 60 ms"
            },
            log.Messages(LogLevel.Debug));

        // 新周期重新计数，没有再被调用的目标不写汇总。
        log.Clear();
        time.Advance(RecognitionDiagnostics.SummaryInterval);
        diagnostics.ReportMatch("battle-chat.png", null, 0.97, 0.96, 1, TimeSpan.FromMilliseconds(6));

        CollectionAssert.AreEqual(
            new[] { "匹配汇总 battle-chat：30 秒内 1 次，命中 1 次，变化 0 次，分数 0.970~0.970，阈值 0.96，平均 6.0 ms，最慢 6.0 ms" },
            log.Messages(LogLevel.Debug));
    }

    [TestMethod]
    public void FlushWritesPendingSummaryAndResetStartsOver()
    {
        var (diagnostics, log, time) = Create();
        diagnostics.ReportMatch("battle-chat.png", null, 0.98, 0.96, 1, Fast);
        time.Advance(TimeSpan.FromSeconds(12));
        log.Clear();

        diagnostics.Flush();
        diagnostics.Flush();

        CollectionAssert.AreEqual(
            new[] { "匹配汇总 battle-chat：12 秒内 1 次，命中 1 次，变化 1 次，分数 0.980~0.980，阈值 0.96，平均 3.0 ms，最慢 3.0 ms" },
            log.Messages(LogLevel.Debug));

        // Flush 之后结果没变就不再记；Reset 之后下一次识别重新作为首次结果记录。
        log.Clear();
        diagnostics.ReportMatch("battle-chat.png", null, 0.98, 0.96, 1, Fast);
        Assert.AreEqual(0, log.Count(LogLevel.Debug));

        diagnostics.Reset();
        log.Clear();
        diagnostics.ReportMatch("battle-chat.png", null, 0.98, 0.96, 1, Fast);
        Assert.AreEqual(1, log.Count(LogLevel.Debug));
    }

    [TestMethod]
    [DataRow("1920x1080/battle-button-skill.png", "battle-skill-button", "battle-button-skill@battle-skill-button")]
    [DataRow(@"1920x1080\magic-point.png", "magic-point", "magic-point")]
    [DataRow("2048x1152/stamina-crafting/map.png", "", "stamina-crafting/map")]
    [DataRow("2048x1152/gardening/entrance.png", null, "gardening/entrance")]
    [DataRow(@"C:\App\Configuration\RecognitionAssets\ImageMatching\1920x1080\battle-chat.png", null, "battle-chat")]
    [DataRow(@"D:\Temp\sample.v2.png", null, "sample.v2")]
    [DataRow("battle-chat", " chat ", "battle-chat@chat")]
    public void FormatsShortTargetNames(string templatePath, string? regionId, string expected)
    {
        Assert.AreEqual(expected, RecognitionDiagnostics.FormatMatchTarget(templatePath, regionId));
    }

    [TestMethod]
    public void RepeatedFailureIsLoggedOnFirstOccurrenceAndThenOncePerInterval()
    {
        var time = new ManualTimeProvider();
        var gate = new RepeatedFailureGate(TimeSpan.FromSeconds(30), time);
        Assert.AreEqual(0, gate.Reset());

        Assert.IsTrue(gate.TryEnter(out var skipped));
        Assert.AreEqual(0, skipped);
        for (var index = 0; index < 5; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.IsFalse(gate.TryEnter(out _));
        }

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.IsTrue(gate.TryEnter(out skipped));
        Assert.AreEqual(5, skipped);

        // 恢复后返回这一轮出现的总次数，再出故障时重新从第一条开始记录。
        Assert.AreEqual(7, gate.Reset());
        Assert.AreEqual(0, gate.Reset());
        Assert.IsTrue(gate.TryEnter(out skipped));
        Assert.AreEqual(0, skipped);
    }

    private static (RecognitionDiagnostics Diagnostics, RecordingLogger Log, ManualTimeProvider Time) Create()
    {
        var log = new RecordingLogger();
        var time = new ManualTimeProvider();
        return (new RecognitionDiagnostics(log, time), log, time);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class RecordingLogger : ILogger<RecognitionDiagnostics>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _entries.Add((logLevel, formatter(state, exception)));

        public int Count(LogLevel level) => _entries.Count(entry => entry.Level == level);

        public string[] Messages(LogLevel level) =>
            _entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToArray();

        public void Clear() => _entries.Clear();
    }
}
