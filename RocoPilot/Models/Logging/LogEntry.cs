namespace RocoPilot.Models.Logging;

public enum LogSeverity { Debug, Information, Warning, Error, Fatal }

public sealed record LogEntry(long Sequence, DateTimeOffset Timestamp, LogSeverity Severity,
    string Source, string Message, string? Exception);
