using System.Text.RegularExpressions;

namespace RocoPilot.Services.IndependentTasks;

/// <summary>
/// 任务结果的 Windows 通知文案。标题只说哪个任务、成没成；正文说结果或原因。
/// 灵动岛有自己的三行约定，文案不从这里取。
/// </summary>
internal static partial class TaskNotificationText
{
    public static (string Title, string Message) ChallengeCompleted(string taskName, string? target, int count)
        => ($"{taskName}完成", string.IsNullOrWhiteSpace(target) ? $"成功 {count} 次" : $"{target} · 成功 {count} 次");

    public static (string Title, string Message) TaskFailed(string taskName, string? reason)
        => ($"{taskName}未完成", Reason(reason));

    public static (string Title, string Message) DailyCompleted(IReadOnlyList<(string Name, string? Outcome)> items)
    {
        var skipped = items.Where(item => IsSkipped(item.Outcome)).ToArray();
        if (skipped.Length == 0) return ("每日清单完成", $"共 {items.Count} 项，全部完成。");
        // 通知正文最多显示四行，跳过的原因比逐项罗列更值得占位置。
        return ("每日清单完成", $"完成 {items.Count - skipped.Length} 项，跳过 {skipped.Length} 项。\n跳过："
            + string.Join("、", skipped.Select(item => $"{item.Name}（{Detail(item.Outcome)}）")));
    }

    public static (string Title, string Message) DailyFailed(string failedTask, string? reason,
        IReadOnlyList<(string Name, string? Outcome)> items)
    {
        var skipped = items.Count(item => IsSkipped(item.Outcome));
        var pending = items.Count(item => item.Outcome is null);
        var done = items.Count(item => item.Outcome is not null && !IsSkipped(item.Outcome) && !item.Outcome.StartsWith("失败"));
        var failed = items.Count(item => item.Outcome?.StartsWith("失败") == true);
        var counts = new[]
        {
            failed > 1 ? $"共失败 {failed} 项" : null,
            done > 0 ? $"已完成 {done} 项" : null,
            skipped > 0 ? $"跳过 {skipped} 项" : null,
            pending > 0 ? $"未执行 {pending} 项" : null
        }.Where(text => text is not null).ToArray();
        return ("每日清单未完成", $"{failedTask}：{Reason(reason)}"
            + (counts.Length == 0 ? string.Empty : $"\n{string.Join("，", counts)}。"));
    }

    /// <summary>出错原因来自异常消息：去掉模板路径一类只对排查有用的内容，句末统一为一个句号。</summary>
    public static string Reason(string? message)
    {
        var text = TechnicalDetail().Replace(message ?? string.Empty, string.Empty).Trim().TrimEnd('。', '.', ' ');
        return text.Length == 0 ? "原因见日志页。" : text + "。";
    }

    private static bool IsSkipped(string? outcome) => outcome?.StartsWith("跳过") == true;

    private static string Detail(string? outcome)
    {
        var index = outcome?.IndexOf('：') ?? -1;
        return index < 0 ? outcome ?? string.Empty : outcome![(index + 1)..];
    }

    [GeneratedRegex(@"（[^（）]*(\.png|[\\/])[^（）]*）")]
    private static partial Regex TechnicalDetail();
}
