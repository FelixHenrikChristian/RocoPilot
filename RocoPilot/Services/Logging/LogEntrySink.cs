using RocoPilot.Models.Logging;

using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace RocoPilot.Services.Logging;

internal sealed class LogEntrySink(LogEntryStore entries) : ILogEventSink
{
    // 与文件日志的 {Message:lj} 一致，字符串参数不带引号。
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}");

    public void Emit(LogEvent logEvent)
    {
        using var message = new StringWriter();
        MessageFormatter.Format(logEvent, message);

        var source = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
            && value is ScalarValue { Value: string name }
                ? name
                : string.Empty;

        entries.Add(
            logEvent.Timestamp,
            ToSeverity(logEvent.Level),
            source,
            message.ToString(),
            logEvent.Exception?.ToString());
    }

    private static LogSeverity ToSeverity(LogEventLevel level) => level switch
    {
        LogEventLevel.Fatal => LogSeverity.Fatal,
        LogEventLevel.Error => LogSeverity.Error,
        LogEventLevel.Warning => LogSeverity.Warning,
        _ => LogSeverity.Information
    };
}
