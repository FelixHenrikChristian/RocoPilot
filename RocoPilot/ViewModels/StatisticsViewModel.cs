using CommunityToolkit.Mvvm.ComponentModel;

namespace RocoPilot.ViewModels;

public partial class StatisticsViewModel : ObservableRecipient
{
    public IReadOnlyList<AccountStatisticsOption> Accounts { get; } =
    [
        new("106947084"),
        new("240186173"),
        new("613908452"),
    ];

    public AccountStatisticsOption? SelectedAccount
    {
        get;
        set;
    }

    public IReadOnlyList<ShinyScopeOption> ShinyScopes { get; } =
    [
        new("全部"),
        new("S1"),
        new("S2"),
        new("S3"),
    ];

    private int _selectedSeasonIndex;

    public int SelectedSeasonIndex
    {
        get => _selectedSeasonIndex;
        set
        {
            var nextIndex = Math.Clamp(value, 0, Seasons.Count - 1);
            SetProperty(ref _selectedSeasonIndex, nextIndex);
        }
    }

    private int _selectedShinyScopeIndex;

    public int SelectedShinyScopeIndex
    {
        get => _selectedShinyScopeIndex;
        set
        {
            var nextIndex = Math.Clamp(value, 0, ShinyScopes.Count - 1);
            if (SetProperty(ref _selectedShinyScopeIndex, nextIndex))
            {
                OnPropertyChanged(nameof(SelectedShinyCounts));
                OnPropertyChanged(nameof(TotalSelectedShiny));
                OnPropertyChanged(nameof(SelectedShinyDateDisplay));
            }
        }
    }

    public IReadOnlyList<SpiritCountItem> AllShinyCounts { get; } =
    [
        new("已垫", 72),
        new("克拉拉", 38),
        new("瓦尔特", 82),
        new("但战斗还未结束", 19),
        new("时节不居", 80),
        new("彦卿", 83),
        new("制胜的瞬间", 75),
        new("姬子", 79),
        new("银河铁道之夜", 16),
        new("白露", 46),
        new("万敌", 74),
        new("布洛妮娅", 22),
    ];

    public int TotalAllShiny => AllShinyCounts.Sum(item => item.Count);

    public IReadOnlyList<SpiritCountItem> SelectedShinyCounts => SelectedShinyScopeIndex == 0
        ? AllShinyCounts
        : Seasons[SelectedShinyScopeIndex - 1].ShinyCounts;

    public int TotalSelectedShiny => SelectedShinyCounts.Sum(item => item.Count);

    public string SelectedShinyDateDisplay => SelectedShinyScopeIndex == 0
        ? $"{Seasons[0].DateRangeStart}-{Seasons[^1].DateRangeEnd}"
        : Seasons[SelectedShinyScopeIndex - 1].SeasonDateDisplay;

    public IReadOnlyList<SeasonStatisticsGroup> Seasons { get; } =
    [
        new(
            "S1赛季",
            "2025/3/26-2025/5/21",
            [
                new("已垫", 6),
                new("银狼LV.999", 60),
                new("银狼", 74),
                new("不死途", 80),
                new("万敌", 79),
                new("布洛妮娅", 80),
                new("昔涟", 81),
                new("白厄", 80),
                new("克拉拉", 35),
                new("彦卿", 73),
                new("镜流", 68),
            ],
            [
                new("已垫", 2),
                new("银狼LV.999", 1),
                new("银狼", 3),
                new("不死途", 1),
                new("布洛妮娅", 2),
                new("昔涟", 4),
                new("白厄", 1),
                new("克拉拉", 2),
            ]),
        new(
            "S2赛季",
            "2025/5/22-2025/7/16",
            [
                new("已垫", 52),
                new("银河铁道之夜", 70),
                new("回到大地的飞行", 11),
                new("纵然山河万程", 72),
                new("黎明恰如此燃烧", 68),
                new("血火啊，燃烧前路", 56),
                new("命运从未公平", 67),
                new("那无数个春天", 67),
                new("流逝的岸", 67),
                new("时节不居", 68),
                new("纯粹思维的洗礼", 72),
                new("到不了的彼岸", 43),
            ],
            [
                new("银河铁道之夜", 2),
                new("回到大地的飞行", 1),
                new("纵然山河万程", 1),
                new("黎明恰如此燃烧", 3),
                new("血火啊，燃烧前路", 1),
                new("命运从未公平", 2),
                new("那无数个春天", 1),
                new("纯粹思维的洗礼", 2),
            ]),
        new(
            "S3赛季",
            "2025/7/17-2025/9/10",
            [
                new("已垫", 18),
                new("花火", 44),
                new("饮月君", 77),
                new("飞霄", 61),
                new("砂金", 58),
                new("知更鸟", 80),
                new("波提欧", 32),
                new("黄泉", 70),
                new("流萤", 65),
                new("翡翠", 27),
            ],
            [
                new("花火", 1),
                new("饮月君", 2),
                new("飞霄", 1),
                new("砂金", 2),
                new("知更鸟", 1),
                new("黄泉", 3),
            ]),
    ];

    public StatisticsViewModel()
    {
        SelectedAccount = Accounts[0];
    }
}

public sealed record AccountStatisticsOption(string Uid)
{
    public string DisplayName => Uid;
}

public sealed record ShinyScopeOption(string Name);

public sealed class SeasonStatisticsGroup
{
    public SeasonStatisticsGroup(
        string name,
        string dateRange,
        IReadOnlyList<SpiritCountItem> pollutionCounts,
        IReadOnlyList<SpiritCountItem> shinyCounts)
    {
        Name = name;
        DateRange = dateRange;
        PollutionCounts = pollutionCounts;
        ShinyCounts = shinyCounts;
    }

    public string Name
    {
        get;
    }

    public string DateRange
    {
        get;
    }

    public string SeasonDateDisplay => DateRange;

    public string DateRangeStart => DateRangeSeparatorIndex < 0
        ? DateRange
        : DateRange[..DateRangeSeparatorIndex];

    public string DateRangeEnd => DateRangeSeparatorIndex < 0
        ? DateRange
        : DateRange[(DateRangeSeparatorIndex + 1)..];

    public string EncounterTitle => $"{SeasonCode}奇遇";

    private string SeasonCode => Name.Replace("赛季", string.Empty);

    private int DateRangeSeparatorIndex => DateRange.IndexOf('-');

    public IReadOnlyList<SpiritCountItem> PollutionCounts
    {
        get;
    }

    public IReadOnlyList<SpiritCountItem> ShinyCounts
    {
        get;
    }

    public int PollutionTotal => PollutionCounts.Sum(item => item.Count);

    public int ShinyTotal => ShinyCounts.Sum(item => item.Count);
}

public sealed class SpiritCountItem
{
    private const double DefaultPityThreshold = 80;

    public SpiritCountItem(string name, int count, double pityThreshold = DefaultPityThreshold)
    {
        Name = name;
        Count = count;
        PityThreshold = pityThreshold;
    }

    public string Name
    {
        get;
    }

    public int Count
    {
        get;
    }

    public double PityThreshold
    {
        get;
    }

    public double ProgressRatio => PityThreshold <= 0
        ? 0
        : Math.Clamp(Count / PityThreshold, 0, 1);
}
