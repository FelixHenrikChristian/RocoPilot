namespace RocoPilot.Models.Runtime;

/// <summary>
/// 独立任务配置与本次扫描获得的花种选项。
/// </summary>
public sealed class IndependentTaskSettings
{
    public const int MinimumRunCount = 1;
    public const int MaximumRunCount = 99;
    public const int DefaultBossBattleRunCount = 3;
    public const int DefaultLegendaryChallengeRunCount = 3;
    public const int DefaultFlowerSeedRunCount = 6;

    public static IReadOnlyList<string> LegendaryTargets { get; } = Array.AsReadOnly(new[] { "圣羽翼王", "龙息帕尔" });

    public string LegendaryTargetName { get; set; } = LegendaryTargets[0];
    public bool UseLegendaryMarkWhenInsufficient { get; set; } = true;

    public int BossBattleRunCount
    {
        get;
        set;
    } = DefaultBossBattleRunCount;

    public int LegendaryChallengeRunCount
    {
        get;
        set;
    } = DefaultLegendaryChallengeRunCount;

    public int FlowerSeedRunCount { get; set; } = DefaultFlowerSeedRunCount;

    public int FlowerSeedTargetNumber { get; set; }

    public List<FlowerSeedOption> FlowerSeedOptions { get; set; } = [];

    public static IndependentTaskSettings CreateDefault()
    {
        return new IndependentTaskSettings();
    }

    public IndependentTaskSettings Clone()
    {
        return new IndependentTaskSettings
        {
            BossBattleRunCount = BossBattleRunCount,
            LegendaryChallengeRunCount = LegendaryChallengeRunCount,
            LegendaryTargetName = LegendaryTargetName,
            UseLegendaryMarkWhenInsufficient = UseLegendaryMarkWhenInsufficient,
            FlowerSeedRunCount = FlowerSeedRunCount,
            FlowerSeedTargetNumber = FlowerSeedTargetNumber,
            FlowerSeedOptions = [.. FlowerSeedOptions]
        };
    }

    public IndependentTaskSettings Normalize()
    {
        return new IndependentTaskSettings
        {
            BossBattleRunCount = Math.Clamp(BossBattleRunCount, MinimumRunCount, MaximumRunCount),
            LegendaryChallengeRunCount = Math.Clamp(LegendaryChallengeRunCount, MinimumRunCount, MaximumRunCount),
            LegendaryTargetName = LegendaryTargets.Contains(LegendaryTargetName) ? LegendaryTargetName : LegendaryTargets[0],
            UseLegendaryMarkWhenInsufficient = UseLegendaryMarkWhenInsufficient,
            FlowerSeedRunCount = Math.Clamp(FlowerSeedRunCount, MinimumRunCount, MaximumRunCount),
            FlowerSeedTargetNumber = FlowerSeedTargetNumber,
            FlowerSeedOptions = [.. FlowerSeedOptions ?? []]
        };
    }
}
