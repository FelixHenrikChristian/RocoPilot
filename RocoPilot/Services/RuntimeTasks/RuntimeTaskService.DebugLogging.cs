using System.Globalization;

using Microsoft.Extensions.Logging;

using RocoPilot.Helpers;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private const int DeduplicatedDebugLogSummaryMinimumRepeatCount = 3;
    private static readonly HashSet<string> SuppressedRecognitionDebugLogCategories = new(StringComparer.Ordinal)
    {
        "runtime-ocr-skip-busy",
        "ocr-skip-outside-frame",
        "ocr-skip-method-unavailable",
        "ocr-result",
        "template-skip-missing-template",
        "template-skip-outside-frame",
        "template-result",
        "game-state-magic-point-active",
        "game-state-magic-point-world",
        "runtime-encounter-tip-filter",
        "runtime-shiny-tip-filter",
        "encounter-tip-filter",
        "encounter-tip-enemy-filter",
        "encounter-tip-missing-enemy",
        "shiny-tip-filter",
        "shiny-enemy-filter",
        "shiny-missing-enemy",
        "encounter-name-transition-placeholder",
        "encounter-name-transition-spirit",
        "encounter-duplicate-suppression",
        "shiny-duplicate-suppression",
        "auto-battle-encounter-relieved-transition-filter",
        "auto-battle-encounter-relieved-tip-filter",
        "auto-battle-shiny-filter",
        "auto-battle-skill-selection-placeholder",
        "auto-battle-skill-selection-enemy-missing",
        "auto-battle-skill-selection-enemy",
        "auto-battle-skill-selection-transition"
    };

    private readonly object _deduplicatedDebugLogLock = new();
    private readonly Dictionary<string, DeduplicatedDebugLogState> _deduplicatedDebugLogs = [];

    private void LogDebugOncePerValue(
        string key,
        string fingerprint,
        string message,
        params object?[] args)
    {
        if (ShouldSuppressRecognitionDebugLog(key))
        {
            return;
        }

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

    private static bool ShouldSuppressRecognitionDebugLog(string key)
    {
        var separatorIndex = key.IndexOf(':', StringComparison.Ordinal);
        var category = separatorIndex < 0
            ? key
            : key[..separatorIndex];
        return SuppressedRecognitionDebugLogCategories.Contains(category);
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
