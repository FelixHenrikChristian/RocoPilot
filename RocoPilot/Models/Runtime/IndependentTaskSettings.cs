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
            FlowerSeedTargetNumber = FlowerSeedTargetNumber,
            FlowerSeedOptions = [.. FlowerSeedOptions ?? []]
        };
    }
}
