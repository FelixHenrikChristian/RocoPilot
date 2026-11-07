using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Models.Runtime;

namespace RocoPilot.ViewModels;

public partial class DailyTaskItemViewModel(DailyTaskKind kind) : ObservableObject
{
    public DailyTaskKind Kind { get; } = kind;

    public IndependentTaskKind? TaskKind => Kind switch
    {
        DailyTaskKind.StaminaCrafting => IndependentTaskKind.StaminaCrafting,
        DailyTaskKind.LegendaryChallenge => IndependentTaskKind.LegendaryChallenge,
        DailyTaskKind.FlowerSeedChallenge => IndependentTaskKind.FlowerSeedChallenge,
        _ => null
    };

    public bool IsAvailable => TaskKind is not null;

    public string Title => Kind switch
    {
        DailyTaskKind.StaminaCrafting => "体力合成",
        DailyTaskKind.Mail => "邮件领取",
        DailyTaskKind.LegendaryChallenge => "传说精灵挑战",
        DailyTaskKind.FlowerSeedChallenge => "花种挑战",
        DailyTaskKind.Gardening => "自动种菜",
        DailyTaskKind.BattlePassRewards => "通行证奖励",
        DailyTaskKind.Inspiration => "自动灵感",
        DailyTaskKind.Sell => "自动出售",
        DailyTaskKind.EveningRest => "晚风小憩",
        _ => string.Empty
    };

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "待接入";

    [ObservableProperty]
    public partial bool CanToggle { get; set; }
}
