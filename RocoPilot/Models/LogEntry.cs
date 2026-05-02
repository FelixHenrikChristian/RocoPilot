using System;

using Serilog.Events;

namespace RocoPilot.Models;

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public LogEventLevel Level { get; init; }
    public string SourceContext { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Exception { get; init; }

    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");

    public string LevelText => Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "LOG",
    };

    public string ShortSource
    {
        get
        {
            if (string.IsNullOrEmpty(SourceContext))
            {
                return string.Empty;
            }

            var idx = SourceContext.LastIndexOf('.');
            return idx >= 0 ? SourceContext[(idx + 1)..] : SourceContext;
        }
    }
}
