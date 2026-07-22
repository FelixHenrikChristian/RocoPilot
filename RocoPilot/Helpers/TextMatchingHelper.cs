using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RocoPilot.Helpers;

public static class TextMatchingHelper
{
    private const string CommonSymbols = "，。？！：；、,.!?:;+-*/%()（）[]{}<>《》【】「」“”\"'~·";

    private static readonly Regex SpiritVariantSuffixRegex = new(
        "[（(][^（）()]*[）)]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    public static int CountChineseCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsChineseIdeograph(rune.Value))
            {
                count++;
            }
        }

        return count;
    }

    public static string CleanSpiritName(string? text)
    {
        return NormalizeSpiritNameInput(text);
    }

    public static string NormalizeSpiritNameInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Normalize(NormalizationForm.FormKC).Trim();
    }

    public static string NormalizeSpiritNameForDisplay(string? text)
    {
        var normalized = NormalizeSpiritNameInput(text);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var withoutVariantSuffix = SpiritVariantSuffixRegex.Replace(normalized, string.Empty).Trim();
        return withoutVariantSuffix.Length == 0 ? normalized : withoutVariantSuffix;
    }

    public static string NormalizeSpiritNameForMatching(string? text)
    {
        var cleaned = CleanRecognizedText(text);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        var withoutVariantSuffix = SpiritVariantSuffixRegex.Replace(cleaned, string.Empty).Trim();
        return withoutVariantSuffix.Length == 0 ? cleaned : withoutVariantSuffix;
    }

    public static bool AreSameSpiritName(string? left, string? right)
    {
        var normalizedLeft = NormalizeSpiritNameForMatching(left);
        var normalizedRight = NormalizeSpiritNameForMatching(right);
        return normalizedLeft.Length > 0
            && normalizedRight.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
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

    private static bool IsChineseIdeograph(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2EE5F
            or >= 0x30000 and <= 0x323AF;
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
