namespace RocoPilot.Models.Statistics;

public sealed class StatisticsUidDetectionResult
{
    private StatisticsUidDetectionResult(bool success, string? uid, string message)
    {
        Success = success;
        Uid = uid;
        Message = message;
    }

    public bool Success { get; }

    public string? Uid { get; }

    public string Message { get; }

    public static StatisticsUidDetectionResult Detected(string uid)
    {
        return new StatisticsUidDetectionResult(true, uid, $"已识别 UID：{uid}");
    }

    public static StatisticsUidDetectionResult Failed(string message)
    {
        return new StatisticsUidDetectionResult(false, null, message);
    }
}

public static class StatisticsUidRules
{
    private const int MaximumUidLength = 32;

    public static bool TryNormalize(string? value, out string uid)
    {
        uid = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = new string(
            value.Where(character => character is >= '0' and <= '9').ToArray());
        if (normalized.Length == 0
            || normalized.Length > MaximumUidLength)
        {
            return false;
        }

        uid = normalized;
        return true;
    }
}

public enum StatisticsUidSelectionAction
{
    UseRecognizedUid,
    RequireConfirmation
}

public sealed record StatisticsUidSelectionDecision(
    StatisticsUidSelectionAction Action,
    string? SuggestedUid,
    string Message);

public static class StatisticsUidSelectionRules
{
    public static StatisticsUidSelectionDecision Decide(
        StatisticsUidDetectionResult detectionResult,
        string? selectedAccountUid)
    {
        ArgumentNullException.ThrowIfNull(detectionResult);

        if (detectionResult.Success
            && !string.IsNullOrWhiteSpace(detectionResult.Uid)
            && !string.IsNullOrWhiteSpace(selectedAccountUid)
            && string.Equals(
                detectionResult.Uid,
                selectedAccountUid,
                StringComparison.OrdinalIgnoreCase))
        {
            return new StatisticsUidSelectionDecision(
                StatisticsUidSelectionAction.UseRecognizedUid,
                detectionResult.Uid,
                detectionResult.Message);
        }

        return new StatisticsUidSelectionDecision(
            StatisticsUidSelectionAction.RequireConfirmation,
            detectionResult.Success ? detectionResult.Uid : null,
            detectionResult.Message);
    }
}
