namespace RocoPilot.Services.Logging;

public static class LogText
{
    /// <summary>把识别原文压成一行并限制长度，空内容写作 &lt;empty&gt;，方便在日志里一眼看出没识别到。</summary>
    public static string Compact(string? text, int maximumLength = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalized = string.Join(
            " ",
            text
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength]}...";
    }
}
