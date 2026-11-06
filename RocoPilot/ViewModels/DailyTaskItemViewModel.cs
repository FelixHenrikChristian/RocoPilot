using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Models.Runtime;

namespace RocoPilot.ViewModels;

public partial class DailyTaskItemViewModel(DailyTaskKind kind) : ObservableObject
{
    public DailyTaskKind Kind { get; } = kind;

    public IndependentTaskKind? TaskKind => Kind switch
    {
        DailyTaskKind.LegendaryChallenge => IndependentTaskKind.LegendaryChallenge,
        DailyTaskKind.FlowerSeedChallenge => IndependentTaskKind.FlowerSeedChallenge,
        _ => null
    };

    public bool IsAvailable => TaskKind is not null;

    public string Title => Kind switch
    {
        DailyTaskKind.StaminaCrafting => "体力合成",
        DailyTaskKind.Mail => "邮件领取",
        DailyTaskKind.LegendaryChallenge => "传说挑战",
        DailyTaskKind.FlowerSeedChallenge => "花种挑战",
        DailyTaskKind.Gardening => "自动种菜",
        DailyTaskKind.BattlePassRewards => "通行证奖励",
        _ => string.Empty
    };

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "待接入";

    [ObservableProperty]
    public partial bool CanToggle { get; set; }
}
