using RocoPilot.Helpers;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

/// <summary>
/// S3 铅绘奇遇血脉提示解析（赛季专用实现，下赛季可整体删除本文件及 tip 区域）。
/// </summary>
internal static class S3EncounterBloodlineRecognition
{
    public const string SeasonId = "S3";

    public const string QiYiKeyword = "奇异";
    public const string HunXueKeyword = "混乱";
    public const string WuRanKeyword = "污染";
    public const string NormalTraitKeyword = "特性";

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
