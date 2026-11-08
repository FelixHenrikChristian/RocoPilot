namespace RocoPilot.Models.Runtime;

public enum DailyTaskKind
{
    StaminaCrafting,
    Mail,
    LegendaryChallenge,
    FlowerSeedChallenge,
    Gardening,
    BattlePassRewards,
    Inspiration,
    Sell,
    EveningRest
}

public sealed record DailyTaskEntry(DailyTaskKind Kind, bool IsEnabled = false);

/// <summary>每日清单及其专用挑战参数。</summary>
public sealed class DailyTaskSettings
{
    private static readonly DailyTaskKind[] DefaultOrder =
    [
        DailyTaskKind.Mail, DailyTaskKind.FlowerSeedChallenge, DailyTaskKind.LegendaryChallenge,
        DailyTaskKind.StaminaCrafting, DailyTaskKind.Gardening, DailyTaskKind.Inspiration,
        DailyTaskKind.Sell, DailyTaskKind.EveningRest, DailyTaskKind.BattlePassRewards
    ];

    public IndependentTaskSettings? Challenges { get; set; }

    public List<DailyTaskEntry> Tasks { get; set; } = [];

    public DailyTaskSettings Normalize()
    {
        var tasks = (Tasks ?? []).Where(task => task is not null && Enum.IsDefined(task.Kind))
            .DistinctBy(task => task.Kind).ToList();
        foreach (var kind in DefaultOrder)
        {
            if (!tasks.Any(task => task.Kind == kind)) tasks.Add(new(kind));
        }

        return new() { Tasks = tasks, Challenges = Challenges };
    }
}
