namespace RocoPilot.Models.Runtime;

/// <summary>
/// 独立任务配置。首领战斗与传说精灵挑战体力限次，执行次数用于控制单次启动的挑战场数。
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

    public static IndependentTaskSettings CreateDefault()
    {
        return new IndependentTaskSettings();
    }

    public IndependentTaskSettings Clone()
    {
        return new IndependentTaskSettings
        {
            BossBattleRunCount = BossBattleRunCount,
            LegendaryChallengeRunCount = LegendaryChallengeRunCount
        };
    }

    public IndependentTaskSettings Normalize()
    {
        return new IndependentTaskSettings
        {
            BossBattleRunCount = Math.Clamp(BossBattleRunCount, MinimumRunCount, MaximumRunCount),
            LegendaryChallengeRunCount = Math.Clamp(LegendaryChallengeRunCount, MinimumRunCount, MaximumRunCount)
        };
    }
}
