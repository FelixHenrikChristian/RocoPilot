using RocoPilot.Models.Logging;

namespace RocoPilot.Services.Logging;

public readonly record struct LogEntryCounts(int Information, int Warning, int Error)
{
    public int Total => Information + Warning + Error;
}

/// <summary>
/// <paramref name="FirstSequence"/> 之前的记录已被移出；下次从 <paramref name="NextSequence"/> 继续读取。
/// </summary>
public sealed record LogEntryBatch(IReadOnlyList<LogEntry> Entries, long FirstSequence, long NextSequence,
    LogEntryCounts Counts);

// 只保留本次运行最近的日志供日志页展示，完整记录以日志文件为准。
public sealed class LogEntryStore
{
    public const int DefaultCapacity = 5000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly int _capacity;
    private long _nextSequence;
    private int _information, _warning, _error;

    public LogEntryStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    // 在写日志的线程上触发，订阅方自行切回界面线程。
    public event EventHandler? Changed;

    public LogEntry Add(DateTimeOffset timestamp, LogSeverity severity, string source, string message,
        string? exception = null)
    {
        LogEntry entry;
        lock (_gate)
        {
            entry = new LogEntry(_nextSequence++, timestamp, severity, source, message, exception);
            _entries.Enqueue(entry);
            AdjustCount(severity, 1);
            if (_entries.Count > _capacity)
            {
                AdjustCount(_entries.Dequeue().Severity, -1);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public LogEntryBatch ReadFrom(long sequence)
    {
        lock (_gate)
        {
            var firstSequence = _nextSequence - _entries.Count;
            var skip = (int)Math.Clamp(sequence - firstSequence, 0, _entries.Count);
            return new LogEntryBatch(
                _entries.Skip(skip).ToArray(),
                firstSequence,
                _nextSequence,
                new LogEntryCounts(_information, _warning, _error));
        }
    }

    private void AdjustCount(LogSeverity severity, int delta)
    {
        switch (severity)
        {
            case LogSeverity.Information:
                _information += delta;
                break;
            case LogSeverity.Warning:
                _warning += delta;
                break;
            default:
                _error += delta;
                break;
        }
    }
}
