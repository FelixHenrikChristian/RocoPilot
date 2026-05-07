using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

internal static class TextRecognitionResultFactory
{
    public static TextRecognitionResult Create(
        TextRecognitionMethod method,
        string methodName,
        string? languageName,
        string text)
    {
        var lines = text
            .ReplaceLineEndings()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return new TextRecognitionResult(
            method,
            methodName,
            languageName,
            lines,
            lines.Sum(CountTextUnits));
    }

    private static int CountTextUnits(string text)
    {
        var wordCount = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Length;

        return wordCount > 1
            ? wordCount
            : text.Count(character => !char.IsWhiteSpace(character));
    }
}
