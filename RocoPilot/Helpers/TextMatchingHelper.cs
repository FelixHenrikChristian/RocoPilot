using System.Globalization;
using System.Text;

namespace RocoPilot.Helpers;

public static class TextMatchingHelper
{
    private const string CommonSymbols = "，。？！：；、,.!?:;+-*/%()[]{}<>《》【】「」“”\"'~·";

    public static string CleanRecognizedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character)
                || char.IsControl(character)
                || char.IsSurrogate(character))
            {
                continue;
            }

            if (IsTextCharacter(character) || CommonSymbols.Contains(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static bool IsSimilar(
        string? actual,
        string? expected,
        double threshold,
        out double similarity)
    {
        similarity = CalculateSimilarity(actual, expected);
        return similarity >= Math.Clamp(threshold, 0, 1);
    }

    public static double CalculateSimilarity(string? actual, string? expected)
    {
        var source = CleanRecognizedText(actual);
        var target = CleanRecognizedText(expected);

        if (source.Length == 0 && target.Length == 0)
        {
            return 1;
        }

        if (source.Length == 0 || target.Length == 0)
        {
            return 0;
        }

        if (source.Contains(target, StringComparison.OrdinalIgnoreCase)
            || target.Contains(source, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var wholeTextSimilarity = CalculateNormalizedLevenshteinSimilarity(source, target);
        if (source.Length <= target.Length)
        {
            return wholeTextSimilarity;
        }

        var bestWindowSimilarity = 0d;
        for (var start = 0; start <= source.Length - target.Length; start++)
        {
            var window = source.Substring(start, target.Length);
            bestWindowSimilarity = Math.Max(
                bestWindowSimilarity,
                CalculateNormalizedLevenshteinSimilarity(window, target));
        }

        return Math.Max(wholeTextSimilarity, bestWindowSimilarity);
    }

    private static bool IsTextCharacter(char character)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(character);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber;
    }

    private static double CalculateNormalizedLevenshteinSimilarity(string source, string target)
    {
        var maxLength = Math.Max(source.Length, target.Length);
        if (maxLength == 0)
        {
            return 1;
        }

        var distance = CalculateLevenshteinDistance(source, target);
        return 1d - distance / (double)maxLength;
    }

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        if (source.Length == 0)
        {
            return target.Length;
        }

        if (target.Length == 0)
        {
            return source.Length;
        }

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];

        for (var column = 0; column <= target.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= source.Length; row++)
        {
            current[0] = row;

            for (var column = 1; column <= target.Length; column++)
            {
                var substitutionCost = char.ToUpperInvariant(source[row - 1]) == char.ToUpperInvariant(target[column - 1])
                    ? 0
                    : 1;

                current[column] = Math.Min(
                    Math.Min(
                        current[column - 1] + 1,
                        previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }
}
