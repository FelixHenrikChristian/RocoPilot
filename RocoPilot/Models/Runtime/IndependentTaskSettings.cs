namespace RocoPilot.Models.Runtime;

/// <summary>
/// 独立任务配置与本次扫描获得的花种选项。
/// </summary>
public sealed class IndependentTaskSettings
{
    public const int MinimumRunCount = 1;
    public const int MaximumRunCount = 99;
    public const int DefaultLegendaryChallengeRunCount = 3;
    public const int DefaultLegendaryDifficulty = 3;
    public const int DefaultFlowerSeedRunCount = 6;

    public static IReadOnlyList<string> LegendaryTargets { get; } = Array.AsReadOnly(new[] { "圣羽翼王", "龙息帕尔" });

    public string LegendaryTargetName { get; set; } = LegendaryTargets[0];
    public int LegendaryDifficulty { get; set; } = DefaultLegendaryDifficulty;
    public bool UseLegendaryMarkWhenInsufficient { get; set; } = true;

    public int LegendaryChallengeRunCount
    {
        get;
        set;
    } = DefaultLegendaryChallengeRunCount;

    public int FlowerSeedRunCount { get; set; } = DefaultFlowerSeedRunCount;
    /// <summary>花种挑战：已手动传送到目标场景，跳过列表导航与地图传送。</summary>
    public bool FlowerSeedAtTargetLocation { get; set; }

    /// <summary>花种挑战：捕捉前优先使用凝练印记（紫色星星）；数量 ≥20 时先确保其选中。</summary>
    public bool FlowerSeedPreferCondensedMark { get; set; } = true;

    /// <summary>传说精灵挑战：已手动传送到目标场景，跳过列表导航与地图传送。</summary>
    public bool LegendaryAtTargetLocation { get; set; }

    {
        return new IndependentTaskSettings();
    }

    public IndependentTaskSettings Clone()
    {
        return new IndependentTaskSettings
        {
            LegendaryChallengeRunCount = LegendaryChallengeRunCount,
            LegendaryTargetName = LegendaryTargetName,
            LegendaryDifficulty = LegendaryDifficulty,
            FlowerSeedAtTargetLocation = FlowerSeedAtTargetLocation,
            FlowerSeedPreferCondensedMark = FlowerSeedPreferCondensedMark,
            LegendaryAtTargetLocation = LegendaryAtTargetLocation,

    public IndependentTaskSettings Normalize(IReadOnlyList<string>? legendaryTargets = null)
    {
        return new IndependentTaskSettings
        {
            LegendaryChallengeRunCount = Math.Clamp(LegendaryChallengeRunCount, MinimumRunCount, MaximumRunCount),
            LegendaryTargetName = (legendaryTargets ?? LegendaryTargets).Contains(LegendaryTargetName)
                ? LegendaryTargetName : legendaryTargets is null ? LegendaryTargets[0] : string.Empty,
            LegendaryDifficulty = LegendaryDifficulty is >= 1 and <= 3 ? LegendaryDifficulty : DefaultLegendaryDifficulty,
            FlowerSeedAtTargetLocation = FlowerSeedAtTargetLocation,
            FlowerSeedPreferCondensedMark = FlowerSeedPreferCondensedMark,
            LegendaryAtTargetLocation = LegendaryAtTargetLocation,
}
