using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Services;

/// <summary>
/// 通用奇遇血脉识别：只检查提示关键词，不限定赛季或完整句式。
/// </summary>
internal static class EncounterBloodlineRecognition
{
    public static IReadOnlyList<string> RegionIds { get; } = [RecognitionRegionIds.BattleBloodlineTip];

    public const string QiYiKeyword = "奇异";
    public const string HunXueKeyword = "混乱";
    public const string WuRanKeyword = "污染";
    public const string NormalTraitKeyword = "特性";

    public static bool IsAvailable(RecognitionRegionConfig config) => config.Regions.FirstOrDefault(region =>
        RuntimeFrameRecognizer.IsRegionMatch(region, RegionIds)) is { Width: > 0, Height: > 0 };

    public static string GetDisplayName(EncounterBloodlineKind kind)
    {
        return kind switch
        {
            EncounterBloodlineKind.QiYi => "奇异",
            EncounterBloodlineKind.HunXue => "混血",
            EncounterBloodlineKind.WuRan => "污染",
            EncounterBloodlineKind.Normal => "普通",
            _ => "未识别"
        };
    }

    public static bool TryParse(string? tipText, out EncounterBloodlineKind kind)
    {
        var cleanedText = TextMatchingHelper.CleanRecognizedText(tipText);
        if (cleanedText.Length == 0
            || TextMatchingHelper.CountChineseCharacters(cleanedText) < 2)
        {
            kind = EncounterBloodlineKind.Unrecognized;
            return false;
        }

        if (cleanedText.Contains(NormalTraitKeyword, StringComparison.OrdinalIgnoreCase))
        {
            kind = EncounterBloodlineKind.Normal;
            return true;
        }

        if (cleanedText.Contains(QiYiKeyword, StringComparison.OrdinalIgnoreCase))
        {
            kind = EncounterBloodlineKind.QiYi;
            return true;
        }

        if (cleanedText.Contains(HunXueKeyword, StringComparison.OrdinalIgnoreCase))
        {
            kind = EncounterBloodlineKind.HunXue;
            return true;
        }

        if (cleanedText.Contains(WuRanKeyword, StringComparison.OrdinalIgnoreCase))
        {
            kind = EncounterBloodlineKind.WuRan;
            return true;
        }

        kind = EncounterBloodlineKind.Unrecognized;
        return false;
    }
}
