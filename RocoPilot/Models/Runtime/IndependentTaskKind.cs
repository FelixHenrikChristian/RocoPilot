namespace RocoPilot.Models.Runtime;

/// <summary>
/// 独立任务种类。独立任务与实时任务分离，按需启动、跑完即止。
/// </summary>
public enum IndependentTaskKind
{
    BossBattle,
    LegendaryChallenge
}
