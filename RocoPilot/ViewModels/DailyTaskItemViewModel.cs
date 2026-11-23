using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Models.Runtime;

namespace RocoPilot.ViewModels;

public partial class DailyTaskItemViewModel(DailyTaskKind kind) : ObservableObject
{
    public DailyTaskKind Kind { get; } = kind;

    public IndependentTaskKind? TaskKind => Kind switch
    {
        DailyTaskKind.BattlePassRewards => IndependentTaskKind.BattlePassRewards,
        DailyTaskKind.Mail => IndependentTaskKind.Mail,
        DailyTaskKind.Sell => IndependentTaskKind.Sell,
        DailyTaskKind.Gardening => IndependentTaskKind.Gardening,
        DailyTaskKind.EveningRest => IndependentTaskKind.EveningRest,
        DailyTaskKind.StaminaCrafting => IndependentTaskKind.StaminaCrafting,
        DailyTaskKind.LegendaryChallenge => IndependentTaskKind.LegendaryChallenge,
        DailyTaskKind.FlowerSeedChallenge => IndependentTaskKind.FlowerSeedChallenge,
        _ => null
    };

    public bool IsAvailable => TaskKind is not null;

    public bool IsUnavailable => !IsAvailable;

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

    /// <summary>Fluent System Icons（Regular 20）里的字形，与 Styles/AppIcons.xaml 同一套字体。</summary>
    public string Glyph => char.ConvertFromUtf32(Kind switch
    {
        DailyTaskKind.StaminaCrafting => 0xF1D7,
        DailyTaskKind.Mail => 0xF506,
        DailyTaskKind.LegendaryChallenge => 0xF06F,
        DailyTaskKind.FlowerSeedChallenge => 0xF4D1,
        DailyTaskKind.Gardening => 0xE66E,
        DailyTaskKind.BattlePassRewards => 0xED7C,
        DailyTaskKind.Inspiration => 0xF4D6,
        DailyTaskKind.Sell => 0xF77C,
        DailyTaskKind.EveningRest => 0xEB0C,
        _ => 0xEC92
    });

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>每日清单正在执行这一项。</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool CanToggle { get; set; }
}
