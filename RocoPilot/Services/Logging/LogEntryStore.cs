using RocoPilot.Models.Logging;

namespace RocoPilot.Services.Logging;

public readonly record struct LogEntryCounts(int Information, int Warning, int Error, int Debug = 0)
{
    // 调试记录默认不显示，单独计数，不算进总数。
    public int Total => Information + Warning + Error;
}

/// <summary>
/// 序号小于 <paramref name="FirstSequence"/> 的信息、警告、错误记录已被移出，
/// 序号小于 <paramref name="FirstDebugSequence"/> 的调试记录已被移出；下次从 <paramref name="NextSequence"/> 继续读取。
/// </summary>
public sealed record LogEntryBatch(IReadOnlyList<LogEntry> Entries, long FirstSequence, long NextSequence,
    LogEntryCounts Counts, long FirstDebugSequence = 0);

// 只保留本次运行最近的日志供日志页展示，完整记录以日志文件为准。
public sealed class LogEntryStore
{
    public const int DefaultCapacity = 5000;
    public const int DefaultDebugCapacity = 5000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();

    // 调试记录数量多得多，单独限量，不挤占给用户看的记录。
    private readonly Queue<LogEntry> _debugEntries = new();
    private readonly int _capacity;
    private readonly int _debugCapacity;
    private long _nextSequence;
    private long _firstSequence;
    private long _firstDebugSequence;
    private int _information, _warning, _error;

    public LogEntryStore(int capacity = DefaultCapacity, int debugCapacity = DefaultDebugCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(debugCapacity);
        _capacity = capacity;
        _debugCapacity = debugCapacity;
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
            if (severity == LogSeverity.Debug)
            {
                _debugEntries.Enqueue(entry);
                if (_debugEntries.Count > _debugCapacity)
                {
                    _firstDebugSequence = _debugEntries.Dequeue().Sequence + 1;
                }
            }
            else
            {
                _entries.Enqueue(entry);
                AdjustCount(severity, 1);
                if (_entries.Count > _capacity)
                {
                    var removed = _entries.Dequeue();
                    AdjustCount(removed.Severity, -1);
                    _firstSequence = removed.Sequence + 1;
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public LogEntryBatch ReadFrom(long sequence)
    {
        lock (_gate)
        {
            return new LogEntryBatch(
                Merge(_entries, _debugEntries, sequence),
                _firstSequence,
                _nextSequence,
                new LogEntryCounts(_information, _warning, _error, _debugEntries.Count),
                _firstDebugSequence);
        }
    }

    // 两个队列各自按序号递增，合并后仍按写入顺序排列。
    private static LogEntry[] Merge(Queue<LogEntry> entries, Queue<LogEntry> debugEntries, long sequence)
    {
        var normal = entries.Where(entry => entry.Sequence >= sequence).ToArray();
        var debug = debugEntries.Where(entry => entry.Sequence >= sequence).ToArray();
        if (debug.Length == 0)
        {
            return normal;
        }

        var merged = new LogEntry[normal.Length + debug.Length];
        for (int index = 0, normalIndex = 0, debugIndex = 0; index < merged.Length; index++)
        {
            merged[index] = debugIndex == debug.Length
                || (normalIndex < normal.Length && normal[normalIndex].Sequence < debug[debugIndex].Sequence)
                    ? normal[normalIndex++]
                    : debug[debugIndex++];
        }

        return merged;
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
