using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FrameTraceSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 20, 15, 0, TimeSpan.FromHours(8));
    private static readonly MessageTemplateParser Parser = new();

    [TestMethod]
    public void KeepsFrameRecordsInMemoryAndReplaysThemBeforeTheWarning()
    {
        var file = new CollectingSink();
        var sink = new FrameTraceSink(file);

        sink.Emit(Event(LogEventLevel.Verbose, "匹配 battle-button-skill：未命中", 0));
        sink.Emit(Event(LogEventLevel.Debug, "游戏场景变化", 1));
        sink.Emit(Event(LogEventLevel.Verbose, "OCR battle-enemy-name：栗鼠", 2));
        sink.Emit(Event(LogEventLevel.Information, "进入战斗", 3));

        // 没出问题时，逐次记录不进文件。
        CollectionAssert.AreEqual(new[] { "游戏场景变化", "进入战斗" }, file.Messages);

        sink.Emit(Event(LogEventLevel.Warning, "截图失败", 4));

        CollectionAssert.AreEqual(
            new[]
            {
                "游戏场景变化",
                "进入战斗",
                "回放开始：以下 2 条是此前最近的逐次识别记录",
                "匹配 battle-button-skill：未命中",
                "OCR battle-enemy-name：栗鼠",
                "回放结束",
                "截图失败"
            },
            file.Messages);
        // 回放的记录保留原来的时间，标记行用触发它的那条记录的时间。
        Assert.AreEqual(Now, file.Events[3].Timestamp);
        Assert.AreEqual(Now.AddSeconds(4), file.Events[2].Timestamp);
        Assert.AreEqual(LogEventLevel.Debug, file.Events[2].Level);
    }

    [TestMethod]
    public void ReplaysOnlyTheMostRecentRecords()
    {
        var file = new CollectingSink();
        var sink = new FrameTraceSink(file, capacity: 2);

        sink.Emit(Event(LogEventLevel.Verbose, "第 1 次", 0));
        sink.Emit(Event(LogEventLevel.Verbose, "第 2 次", 1));
        sink.Emit(Event(LogEventLevel.Verbose, "第 3 次", 2));
        sink.Emit(Event(LogEventLevel.Error, "任务异常终止", 3));

        CollectionAssert.AreEqual(
            new[] { "回放开始：以下 2 条是此前最近的逐次识别记录", "第 2 次", "第 3 次", "回放结束", "任务异常终止" },
            file.Messages);
    }

    [TestMethod]
    public void RepeatedWarningsDoNotReplayAgainUntilCooldownPasses()
    {
        var file = new CollectingSink();
        var sink = new FrameTraceSink(file, replayCooldown: TimeSpan.FromSeconds(30));

        sink.Emit(Event(LogEventLevel.Verbose, "第 1 次", 0));
        sink.Emit(Event(LogEventLevel.Warning, "截图失败", 1));
        sink.Emit(Event(LogEventLevel.Verbose, "第 2 次", 2));
        sink.Emit(Event(LogEventLevel.Warning, "截图失败", 3));
        Assert.AreEqual(5, file.Events.Count);
        Assert.AreEqual("截图失败", file.Messages[^1]);

        // 冷却期间攒下的记录没有丢，下一次回放时一起写出。
        sink.Emit(Event(LogEventLevel.Verbose, "第 3 次", 4));
        sink.Emit(Event(LogEventLevel.Warning, "截图失败", 31));
        CollectionAssert.AreEqual(
            new[] { "回放开始：以下 2 条是此前最近的逐次识别记录", "第 2 次", "第 3 次", "回放结束", "截图失败" },
            file.Messages.Skip(5).ToArray());
    }

    [TestMethod]
    public void WarningWithoutFrameRecordsIsWrittenAlone()
    {
        var file = new CollectingSink();
        var sink = new FrameTraceSink(file);

        sink.Emit(Event(LogEventLevel.Warning, "读取通知设置失败", 0));

        CollectionAssert.AreEqual(new[] { "读取通知设置失败" }, file.Messages);
    }

    [TestMethod]
    public void DisposesTheFileSink()
    {
        var file = new CollectingSink();

        new FrameTraceSink(file).Dispose();

        Assert.IsTrue(file.IsDisposed);
    }

    private static LogEvent Event(LogEventLevel level, string message, int secondsLater) =>
        new(Now.AddSeconds(secondsLater), level, null, Parser.Parse(message), []);

    private sealed class CollectingSink : ILogEventSink, IDisposable
    {
        public List<LogEvent> Events { get; } = [];

        public string[] Messages => Events.Select(logEvent => logEvent.RenderMessage()).ToArray();

        public bool IsDisposed { get; private set; }

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

        public void Dispose() => IsDisposed = true;
    }
}
