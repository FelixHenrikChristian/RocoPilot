using System.Globalization;
using System.Text;

using RocoPilot.Models.Logging;

namespace RocoPilot.Services.Logging;

// 复制和导出沿用日志文件的行格式，贴到反馈里时和文件内容一致。
public static class LogEntryFormatter
{
    public static string Format(LogEntry entry)
    {
        var builder = new StringBuilder();
        Append(builder, entry);
        return builder.ToString();
    }

    public static string Format(IEnumerable<LogEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            Append(builder, entry);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, LogEntry entry)
    {
        builder
            .Append('[')
            .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("] [")
            .Append(GetLevelCode(entry.Severity))
            .Append("] [")
            .Append(entry.Source)
            .Append("] ")
            .Append(entry.Message);

        if (entry.Exception is not null)
        {
            builder.AppendLine().Append(entry.Exception);
        }
    }

    private static string GetLevelCode(LogSeverity severity) => severity switch
    {
        LogSeverity.Fatal => "FTL",
        LogSeverity.Error => "ERR",
        LogSeverity.Warning => "WRN",
        _ => "INF"
    };
}
