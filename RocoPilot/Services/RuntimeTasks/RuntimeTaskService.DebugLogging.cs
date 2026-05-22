using System.Globalization;

using Microsoft.Extensions.Logging;

using RocoPilot.Helpers;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private const int DeduplicatedDebugLogSummaryMinimumRepeatCount = 3;

    private readonly object _deduplicatedDebugLogLock = new();
    private readonly Dictionary<string, DeduplicatedDebugLogState> _deduplicatedDebugLogs = [];

    private void LogDebugOncePerValue(
        string key,
        string fingerprint,
        string message,
        params object?[] args)
    {
        var normalizedFingerprint = string.IsNullOrWhiteSpace(fingerprint)
            ? "<empty>"
            : fingerprint;
        var shouldLog = false;
        string? suppressedFingerprint = null;
        var suppressedRepeatCount = 0;

        lock (_deduplicatedDebugLogLock)
        {
            if (!_deduplicatedDebugLogs.TryGetValue(key, out var state))
            {
                _deduplicatedDebugLogs[key] = new DeduplicatedDebugLogState(normalizedFingerprint);
                shouldLog = true;
            }
            else if (string.Equals(state.Fingerprint, normalizedFingerprint, StringComparison.Ordinal))
            {
                state.RepeatCount++;
            }
            else
            {
                if (state.RepeatCount >= DeduplicatedDebugLogSummaryMinimumRepeatCount)
                {
                    suppressedFingerprint = state.Fingerprint;
                    suppressedRepeatCount = state.RepeatCount;
                }

                state.Fingerprint = normalizedFingerprint;
                state.RepeatCount = 0;
                shouldLog = true;
            }
        }

        if (suppressedRepeatCount > 0)
        {
            _logger.LogDebug(
                "重复 Debug 日志已折叠：Key={LogKey}, Value={Value}, RepeatCount={RepeatCount}",
                key,
                suppressedFingerprint,
                suppressedRepeatCount);
        }

        if (shouldLog)
        {
            _logger.LogDebug(message, args);
        }
    }

    private void ResetDeduplicatedDebugLogs()
    {
        lock (_deduplicatedDebugLogLock)
        {
            _deduplicatedDebugLogs.Clear();
        }
    }

    private static string CreateDebugLogKey(string category, params object?[] parts)
    {
        return $"{category}:{string.Join("|", parts.Select(part => part?.ToString() ?? "<null>"))}";
    }

    private static string CreateTextDebugFingerprint(string? text)
    {
        var cleaned = TextMatchingHelper.CleanRecognizedText(text);
        return cleaned.Length == 0 ? "<empty>" : cleaned;
    }

    private static string CreateBooleanDebugFingerprint(bool value)
    {
        return value ? "true" : "false";
    }

    private static string CreateSimilarityDebugFingerprint(double value)
    {
        return Math.Clamp(value, 0, 1).ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string CreateMatchFilterDebugFingerprint(
        string? text,
        double similarity,
        bool isMatch)
    {
        return string.Join(
            "|",
            CreateTextDebugFingerprint(text),
            CreateSimilarityDebugFingerprint(similarity),
            CreateBooleanDebugFingerprint(isMatch));
    }

    private sealed class DeduplicatedDebugLogState(string fingerprint)
    {
        public string Fingerprint { get; set; } = fingerprint;

        public int RepeatCount { get; set; }
    }
}
