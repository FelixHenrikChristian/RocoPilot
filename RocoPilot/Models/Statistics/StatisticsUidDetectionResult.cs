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

        var normalized = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (normalized.Length == 0
            || normalized.Length > MaximumUidLength
            || normalized.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        uid = normalized;
        return true;
    }
}
