using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace RocoPilot.Services.Logging;

/// <summary>
/// 调试日志文件的入口。逐次识别记录（Verbose）平时只留在内存里，不写文件；
/// 出现警告或错误时，把此前最近的一段补写进文件，方便回看出问题前每一次识别的结果。
/// 偶发问题很难事后复现，所以不设“打开后全量记录”的开关，靠这段回放留住现场。
/// </summary>
internal sealed class FrameTraceSink : ILogEventSink, IDisposable
{
    public const int DefaultCapacity = 300;

    // 连续告警时不反复回放，两次回放之间至少隔这么久。
    public static readonly TimeSpan DefaultReplayCooldown = TimeSpan.FromSeconds(30);

    private static readonly MessageTemplateParser Parser = new();
    private static readonly MessageTemplate ReplayStarted =
        Parser.Parse("回放开始：以下 {Count} 条是此前最近的逐次识别记录");
    private static readonly MessageTemplate ReplayCompleted = Parser.Parse("回放结束");
    private static readonly LogEventProperty Source = new(
        Constants.SourceContextPropertyName, new ScalarValue(typeof(FrameTraceSink).FullName));

    private readonly ILogEventSink _inner;
    private readonly int _capacity;
    private readonly TimeSpan _replayCooldown;
    private readonly Queue<LogEvent> _recent = new();
    private readonly object _gate = new();
    private DateTimeOffset? _lastReplayAt;

    public FrameTraceSink(
        ILogEventSink inner,
        int capacity = DefaultCapacity,
        TimeSpan? replayCooldown = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _inner = inner;
        _capacity = capacity;
        _replayCooldown = replayCooldown ?? DefaultReplayCooldown;
    }

    public void Emit(LogEvent logEvent)
    {
        // 回放和触发它的那条记录要连在一起写，整个过程在锁内完成。
        lock (_gate)
        {
            if (logEvent.Level == LogEventLevel.Verbose)
            {
                _recent.Enqueue(logEvent);
                if (_recent.Count > _capacity)
                {
                    _recent.Dequeue();
                }

                return;
            }

            if (logEvent.Level >= LogEventLevel.Warning && _recent.Count > 0
                && (_lastReplayAt is null || logEvent.Timestamp - _lastReplayAt.Value >= _replayCooldown))
            {
                _lastReplayAt = logEvent.Timestamp;
                _inner.Emit(CreateMarker(logEvent.Timestamp, ReplayStarted,
                    new LogEventProperty("Count", new ScalarValue(_recent.Count))));
                while (_recent.TryDequeue(out var recent))
                {
                    _inner.Emit(recent);
                }

                _inner.Emit(CreateMarker(logEvent.Timestamp, ReplayCompleted));
            }

            _inner.Emit(logEvent);
        }
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    private static LogEvent CreateMarker(
        DateTimeOffset timestamp, MessageTemplate template, params LogEventProperty[] properties) =>
        new(timestamp, LogEventLevel.Debug, null, template, [Source, .. properties]);
}
