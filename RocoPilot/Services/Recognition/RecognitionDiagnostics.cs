using Microsoft.Extensions.Logging;

using RocoPilot.Services.Logging;

namespace RocoPilot.Services.Recognition;

/// <summary>
/// 图像匹配与 OCR 的调试记录。识别每秒发生二十多次，逐次写日志会刷屏，这里统一控量：
/// <list type="bullet">
/// <item>逐次结果记为 Trace，平时只留在内存里，出现警告或错误时回放进调试日志；</item>
/// <item>同一目标的结果变了（命中变未命中、OCR 文字变化）记一条 Debug，连续抖动时限量；</item>
/// <item>每隔 <see cref="SummaryInterval"/> 为每个目标记一条汇总。</item>
/// </list>
/// 只在图像匹配服务和文字识别服务里各上报一次，调用方不传任务名，也不自己判断要不要记。
/// </summary>
public sealed class RecognitionDiagnostics
{
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);

    // 结果连续变化时先放行几条，之后每隔一段时间再放行一条，其余只计数。
    private const int ChangeBurst = 4;
    private static readonly TimeSpan ChangeRefillInterval = TimeSpan.FromSeconds(5);
    private const int MaximumTargets = 256;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Target> _targets = new(StringComparer.Ordinal);
    private long _windowStartedAt;

    public RecognitionDiagnostics(ILogger<RecognitionDiagnostics> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _windowStartedAt = _time.GetTimestamp();
    }

    /// <param name="matchCount">命中的数量；单目标匹配命中为 1，未命中为 0。</param>
    public void ReportMatch(string templatePath, string? regionId, double score, double threshold, int matchCount,
        TimeSpan elapsed)
    {
        var name = FormatMatchTarget(templatePath, regionId);
        var outcome = matchCount switch
        {
            0 => "未命中",
            1 => "命中",
            _ => $"命中 {matchCount} 处"
        };
        var elapsedMs = elapsed.TotalMilliseconds;

        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var target = GetTarget("match:" + name, name, isText: false, now);
            _logger.LogTrace("匹配 {Target}：{Outcome}，分数 {Score:F3}，阈值 {Threshold:F2}，耗时 {ElapsedMs:F1} ms",
                name, outcome, score, threshold, elapsedMs);

            if (target.LastMatchCount != matchCount)
            {
                target.LastMatchCount = matchCount;
                target.Changes++;
                if (TryTakeChangeToken(target, now, out var skipped))
                {
                    if (skipped > 0)
                    {
                        _logger.LogDebug(
                            "匹配变化 {Target}：{Outcome}，分数 {Score:F3}，阈值 {Threshold:F2}，此前另有 {Skipped} 次变化未逐条记录",
                            name, outcome, score, threshold, skipped);
                    }
                    else
                    {
                        _logger.LogDebug("匹配变化 {Target}：{Outcome}，分数 {Score:F3}，阈值 {Threshold:F2}",
                            name, outcome, score, threshold);
                    }
                }
            }

            target.Calls++;
            if (matchCount > 0)
            {
                target.Hits++;
            }

            target.MinimumScore = Math.Min(target.MinimumScore, score);
            target.MaximumScore = Math.Max(target.MaximumScore, score);
            target.Threshold = threshold;
            target.TotalMs += elapsedMs;
            target.SlowestMs = Math.Max(target.SlowestMs, elapsedMs);
            WriteSummariesWhenDue(now);
        }
    }

    public void ReportText(string target, string? text, TimeSpan elapsed)
    {
        var name = string.IsNullOrWhiteSpace(target) ? "<unnamed>" : target.Trim();
        var compact = LogText.Compact(text);
        var isEmpty = string.IsNullOrWhiteSpace(text);
        var elapsedMs = elapsed.TotalMilliseconds;

        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var state = GetTarget("text:" + name, name, isText: true, now);
            _logger.LogTrace("OCR {Target}：{Text}，耗时 {ElapsedMs:F0} ms", name, compact, elapsedMs);

            if (!string.Equals(state.LastText, compact, StringComparison.Ordinal))
            {
                state.LastText = compact;
                state.Changes++;
                if (TryTakeChangeToken(state, now, out var skipped))
                {
                    if (skipped > 0)
                    {
                        _logger.LogDebug("OCR 变化 {Target}：{Text}，此前另有 {Skipped} 次变化未逐条记录",
                            name, compact, skipped);
                    }
                    else
                    {
                        _logger.LogDebug("OCR 变化 {Target}：{Text}", name, compact);
                    }
                }
            }

            state.Calls++;
            if (!isEmpty)
            {
                state.Hits++;
            }

            state.TotalMs += elapsedMs;
            state.SlowestMs = Math.Max(state.SlowestMs, elapsedMs);
            WriteSummariesWhenDue(now);
        }
    }

    /// <summary>立即写出尚未汇总的统计。任务暂停或结束时调用，避免最后一段丢失。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            WriteSummaries(_time.GetTimestamp());
        }
    }

    /// <summary>写出统计并忘掉各目标上一次的结果，下次识别从“首次”开始记录。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            WriteSummaries(_time.GetTimestamp());
            _targets.Clear();
        }
    }

    /// <summary>日志里的目标名：模板去掉分辨率目录和扩展名，在指定识别区域里匹配时附上区域 ID。</summary>
    internal static string FormatMatchTarget(string templatePath, string? regionId)
    {
        var name = (templatePath ?? string.Empty).Replace('\\', '/').Trim();
        const string assetDirectory = "/ImageMatching/";
        var assetIndex = name.LastIndexOf(assetDirectory, StringComparison.OrdinalIgnoreCase);
        if (assetIndex >= 0)
        {
            name = name[(assetIndex + assetDirectory.Length)..];
        }
        else if (name.StartsWith('/') || (name.Length > 1 && name[1] == ':'))
        {
            name = name[(name.LastIndexOf('/') + 1)..];
        }

        var extensionIndex = name.LastIndexOf('.');
        if (extensionIndex > name.LastIndexOf('/'))
        {
            name = name[..extensionIndex];
        }

        var separatorIndex = name.IndexOf('/');
        if (separatorIndex > 0 && IsResolution(name.AsSpan(0, separatorIndex)))
        {
            name = name[(separatorIndex + 1)..];
        }

        if (name.Length == 0)
        {
            name = "<unnamed>";
        }

        var region = regionId?.Trim();
        return string.IsNullOrEmpty(region) || string.Equals(region, name, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}@{region}";
    }

    private static bool IsResolution(ReadOnlySpan<char> text)
    {
        var separatorIndex = text.IndexOf('x');
        return separatorIndex > 0 && separatorIndex < text.Length - 1
            && IsDigits(text[..separatorIndex]) && IsDigits(text[(separatorIndex + 1)..]);

        static bool IsDigits(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private Target GetTarget(string key, string name, bool isText, long now)
    {
        if (_targets.TryGetValue(key, out var target))
        {
            return target;
        }

        // 正常运行时目标数量有限；测试页反复换模板时整体清掉，不让表无限增长。
        if (_targets.Count >= MaximumTargets)
        {
            WriteSummaries(now);
            _targets.Clear();
        }

        target = new Target(name, isText, now);
        _targets[key] = target;
        return target;
    }

    private bool TryTakeChangeToken(Target target, long now, out int skipped)
    {
        var refill = _time.GetElapsedTime(target.TokensUpdatedAt, now) / ChangeRefillInterval;
        target.Tokens = Math.Min(ChangeBurst, target.Tokens + refill);
        target.TokensUpdatedAt = now;
        if (target.Tokens < 1)
        {
            target.SkippedChanges++;
            skipped = 0;
            return false;
        }

        target.Tokens -= 1;
        skipped = target.SkippedChanges;
        target.SkippedChanges = 0;
        return true;
    }

    private void WriteSummariesWhenDue(long now)
    {
        if (_time.GetElapsedTime(_windowStartedAt, now) >= SummaryInterval)
        {
            WriteSummaries(now);
        }
    }

    private void WriteSummaries(long now)
    {
        var seconds = _time.GetElapsedTime(_windowStartedAt, now).TotalSeconds;
        _windowStartedAt = now;
        foreach (var target in _targets.Values)
        {
            if (target.Calls == 0)
            {
                continue;
            }

            var averageMs = target.TotalMs / target.Calls;
            if (target.IsText)
            {
                _logger.LogDebug(
                    "OCR 汇总 {Target}：{Seconds:F0} 秒内 {Calls} 次，非空 {Hits} 次，变化 {Changes} 次，平均 {AverageMs:F0} ms，最慢 {SlowestMs:F0} ms",
                    target.Name, seconds, target.Calls, target.Hits, target.Changes, averageMs, target.SlowestMs);
            }
            else
            {
                _logger.LogDebug(
                    "匹配汇总 {Target}：{Seconds:F0} 秒内 {Calls} 次，命中 {Hits} 次，变化 {Changes} 次，分数 {MinimumScore:F3}~{MaximumScore:F3}，阈值 {Threshold:F2}，平均 {AverageMs:F1} ms，最慢 {SlowestMs:F1} ms",
                    target.Name, seconds, target.Calls, target.Hits, target.Changes, target.MinimumScore,
                    target.MaximumScore, target.Threshold, averageMs, target.SlowestMs);
            }

            target.ResetWindow();
        }
    }

    private sealed class Target(string name, bool isText, long now)
    {
        public string Name { get; } = name;
        public bool IsText { get; } = isText;

        // 上一次的结果，用来判断有没有变化。
        public int LastMatchCount { get; set; } = -1;
        public string? LastText { get; set; }

        public double Tokens { get; set; } = ChangeBurst;
        public long TokensUpdatedAt { get; set; } = now;
        public int SkippedChanges { get; set; }

        // 当前汇总周期内的统计。
        public int Calls { get; set; }
        public int Hits { get; set; }
        public int Changes { get; set; }
        public double MinimumScore { get; set; } = double.MaxValue;
        public double MaximumScore { get; set; } = double.MinValue;
        public double Threshold { get; set; }
        public double TotalMs { get; set; }
        public double SlowestMs { get; set; }

        public void ResetWindow()
        {
            Calls = Hits = Changes = 0;
            MinimumScore = double.MaxValue;
            MaximumScore = double.MinValue;
            TotalMs = SlowestMs = 0;
        }
    }
}
