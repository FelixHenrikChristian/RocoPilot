namespace RocoPilot.Models.Runtime;

public enum DailyTaskKind
{
    StaminaCrafting,
    Mail,
    LegendaryChallenge,
    FlowerSeedChallenge,
    Gardening,
    BattlePassRewards
}

public sealed record DailyTaskEntry(DailyTaskKind Kind, bool IsEnabled = false);

/// <summary>仅保存一份每日清单；挑战参数由独立任务统一管理。</summary>
public sealed class DailyTaskSettings
{
    public List<DailyTaskEntry> Tasks { get; set; } = [];

    public DailyTaskSettings Normalize()
    {
        var kinds = Enum.GetValues<DailyTaskKind>();
        var tasks = (Tasks ?? []).Where(task => task is not null && Enum.IsDefined(task.Kind))
            .DistinctBy(task => task.Kind).ToList();
        foreach (var kind in kinds)
        {
            if (!tasks.Any(task => task.Kind == kind)) tasks.Add(new(kind));
        }

        return new() { Tasks = tasks };
    }
}
