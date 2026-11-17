using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Models.Logging;

namespace RocoPilot.ViewModels;

public partial class LogEntryItemViewModel(LogEntry entry) : ObservableObject
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    public LogEntry Entry { get; } = entry;

    public string Time => Entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string FullTime => Entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string LevelText => Entry.Severity switch
    {
        LogSeverity.Fatal => "致命",
        LogSeverity.Error => "错误",
        LogSeverity.Warning => "警告",
        LogSeverity.Debug => "调试",
        _ => "信息"
    };

    // 行内只显示类名，完整命名空间留给展开后的详情。
    public string Source => Entry.Source[(Entry.Source.LastIndexOf('.') + 1)..];

    // 列表行固定一行，多行消息在展开后完整显示。
    public string Headline => Entry.Message.AsSpan().IndexOfAny(LineBreaks) < 0
        ? Entry.Message
        : string.Join(' ', Entry.Message.Split(
            LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public bool HasException => Entry.Exception is not null;

    public bool IsDebug => Entry.Severity == LogSeverity.Debug;

    public bool IsInformation => Entry.Severity == LogSeverity.Information;

    public bool IsWarning => Entry.Severity == LogSeverity.Warning;

    public bool IsError => Entry.Severity is LogSeverity.Error or LogSeverity.Fatal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    public partial bool IsExpanded { get; set; }

    public bool IsCollapsed => !IsExpanded;
}
