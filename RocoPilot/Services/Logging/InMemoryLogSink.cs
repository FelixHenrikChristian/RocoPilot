using System;
using System.Collections.Generic;
using System.IO;

using RocoPilot.Models;

using Serilog.Core;
using Serilog.Events;

namespace RocoPilot.Services.Logging;

public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _buffer;
    private readonly int _capacity;

    public event Action<LogEntry>? EntryWritten;

    public InMemoryLogSink(int capacity = 2000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _buffer = new Queue<LogEntry>(capacity);
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = Convert(logEvent);

        lock (_gate)
        {
            if (_buffer.Count >= _capacity)
            {
                _buffer.Dequeue();
            }

            _buffer.Enqueue(entry);
        }

        EntryWritten?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
        }
    }

    private static LogEntry Convert(LogEvent e)
    {
        var source = string.Empty;
        if (e.Properties.TryGetValue("SourceContext", out var sc) &&
            sc is ScalarValue sv && sv.Value is string s)
        {
            source = s;
        }

        string message;
        try
        {
            message = e.RenderMessage();
        }
        catch
        {
            message = e.MessageTemplate.Text;
        }

        string? exception = null;
        if (e.Exception != null)
        {
            using var sw = new StringWriter();
            sw.Write(e.Exception.ToString());
            exception = sw.ToString();
        }

        return new LogEntry
        {
            Timestamp = e.Timestamp,
            Level = e.Level,
            SourceContext = source,
            Message = message,
            Exception = exception,
        };
    }
}
