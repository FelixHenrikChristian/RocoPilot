using RocoPilot.Helpers;

namespace RocoPilot.Services;

internal static class BossBattleRecognition
{
    public const string ComboPromptText = "连续释放技能攻击首领";
    public const double ComboPromptMatchThreshold = 0.7;
    public const string EnergyInsufficientText = "能量不足";
    public const double EnergyInsufficientMatchThreshold = 0.7;

    private const int MinimumBossNameChineseCharacterCount = 2;
    private const int MinimumComboPromptTextLength = 6;
    private const int MinimumEnergyInsufficientTextLength = 3;

    public static bool HasRecognizedName(string? text)
    {
        var cleanedText = TextMatchingHelper.CleanRecognizedText(text);
        return TextMatchingHelper.CountChineseCharacters(cleanedText)
            >= MinimumBossNameChineseCharacterCount;
    }

    public static int UpdateConsecutiveNameRecognitionCount(string? text, int currentCount)
    {
        return HasRecognizedName(text)
            ? Math.Max(0, currentCount) + 1
            : 0;
    }

    public static bool IsComboPrompt(string? text, out double similarity)
    {
        var cleanedText = TextMatchingHelper.CleanRecognizedText(text);
        if (cleanedText.Length < MinimumComboPromptTextLength)
        {
            similarity = TextMatchingHelper.CalculateSimilarity(cleanedText, ComboPromptText);
            return false;
        }

        return TextMatchingHelper.IsSimilar(
            cleanedText,
            ComboPromptText,
            ComboPromptMatchThreshold,
            out similarity);
    }

    public static bool IsEnergyInsufficient(string? text, out double similarity)
    {
        var cleanedText = TextMatchingHelper.CleanRecognizedText(text);
        if (cleanedText.Length < MinimumEnergyInsufficientTextLength)
        {
            similarity = TextMatchingHelper.CalculateSimilarity(cleanedText, EnergyInsufficientText);
            return false;
        }

        return TextMatchingHelper.IsSimilar(
            cleanedText,
            EnergyInsufficientText,
            EnergyInsufficientMatchThreshold,
            out similarity);
    }
}
