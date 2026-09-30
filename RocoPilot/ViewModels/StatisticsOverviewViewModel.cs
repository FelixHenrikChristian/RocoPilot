using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;

namespace RocoPilot.ViewModels;

/// <summary>只负责统计快照的展示、筛选和编辑草稿，不修改统计服务中的账号或数据。</summary>
public sealed class StatisticsOverviewViewModel : ObservableObject
{
    private AccountStatisticsData? _account;
    private Func<string, BitmapImage?>? _shinyAvatarResolver;
    private int _selectedSeasonIndex;
    private int _selectedShinyScopeIndex;
    private bool _isRefreshing;

    public IReadOnlyList<AccountStatisticsOption> Accounts { get; private set; } = [];

    public AccountStatisticsOption? SelectedAccount { get; private set; }

    public string SelectedAccountDisplayName => SelectedAccount?.DisplayName ?? "未选择账号";

    public IReadOnlyList<SeasonStatisticsGroup> Seasons { get; private set; } = [];

    public IReadOnlyList<ShinyScopeOption> ShinyScopes { get; private set; } = [new("全部")];

    public IReadOnlyList<SpiritCountItem> AllShinyCounts { get; private set; } = [];

    public IReadOnlyList<PendingShinyCaptureItem> PendingShinyCaptures { get; private set; } = [];

    public PendingShinyCaptureEditor PendingEditor { get; } = new();

    public IReadOnlyList<PendingEncounterItem> PendingEncounters { get; private set; } = [];

    public int PendingEncounterCount => PendingEncounters.Count;

    public Visibility PendingEncounterVisibility => PendingEncounterCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public int SelectedSeasonIndex
    {
        get => _selectedSeasonIndex;
        set
        {
            if (!_isRefreshing && value >= 0 && value < Seasons.Count
                && SetProperty(ref _selectedSeasonIndex, value))
            {
                OnPropertyChanged(nameof(SelectedSeason));
                OnPropertyChanged(nameof(DefaultShinyAddSeasonId));
            }
        }
    }

    public int SelectedShinyScopeIndex
    {
        get => _selectedShinyScopeIndex;
        set
        {
            if (!_isRefreshing && value >= 0 && value < ShinyScopes.Count
                && SetProperty(ref _selectedShinyScopeIndex, value))
            {
                OnPropertyChanged(nameof(SelectedShinyScopeSeasonId));
                OnPropertyChanged(nameof(DefaultShinyAddSeasonId));
                NotifySelectedShinyChanged();
            }
        }
    }

    public SeasonStatisticsGroup? SelectedSeason => Seasons.ElementAtOrDefault(SelectedSeasonIndex);

    public string? SelectedShinyScopeSeasonId => SelectedShinyScopeIndex == 0
        ? null : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.Id;

    public string? DefaultShinyAddSeasonId => SelectedShinyScopeSeasonId
        ?? SelectedSeason?.Id
        ?? Seasons.FirstOrDefault()?.Id;

    public IReadOnlyList<SpiritCountItem> SelectedShinyCounts => SelectedShinyScopeIndex == 0
        ? AllShinyCounts : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.ShinyCounts ?? [];

    public int TotalAllShiny => AllShinyCounts.Sum(item => item.Count);

    public int TotalSelectedShiny => SelectedShinyCounts.Sum(item => item.Count);

    public string SelectedShinyDateDisplay => SelectedShinyScopeIndex == 0
        ? StatisticsProjection.BuildAllSeasonDateDisplay(Seasons)
        : Seasons.ElementAtOrDefault(SelectedShinyScopeIndex - 1)?.SeasonDateDisplay ?? "无记录";

    public int PendingShinyCount => PendingShinyCaptures.Count;

    public PendingShinyCaptureItem? LatestPendingShinyCapture => PendingShinyCaptures
        .FirstOrDefault(item => item.Season != EncounterSeasonTimeline.PendingSeasonId);

    public Visibility PendingShinyBadgeVisibility => PendingShinyCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PendingShinyConfirmationVisibility => LatestPendingShinyCapture is null ? Visibility.Collapsed : Visibility.Visible;

    public string PendingShinySeasonDisplay => LatestPendingShinyCapture?.SeasonDisplay ?? "--";

    public string PendingShinyDetectedAtDisplay => LatestPendingShinyCapture?.DetectedAtDisplay ?? "--";

    public BitmapImage? LatestPendingShinyAvatar => LatestPendingShinyCapture?.Avatar;

    public Visibility LatestPendingShinyAvatarVisibility => LatestPendingShinyAvatar is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LatestPendingShinyAvatarFallbackVisibility => LatestPendingShinyAvatar is null ? Visibility.Visible : Visibility.Collapsed;

    public string PendingShinyQueueDisplay => PendingShinyCount > 1 ? $"还有 {PendingShinyCount - 1} 条待确认" : "当前仅此一条";

    public string PendingShinyNameHint => LatestPendingShinyCapture is { } capture && string.IsNullOrWhiteSpace(capture.Name)
        ? (string.IsNullOrWhiteSpace(capture.RawText) ? "暂未识别到精灵名。" : $"原始识别：{capture.RawText}。")
            + "可手动填写名称，或更新图鉴后再确认。"
        : string.Empty;

    public Visibility PendingShinyNameHintVisibility => PendingShinyNameHint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<ShinyCaptureDetailItem> GetShinyCaptureDetails(SpiritCountItem item) =>
        StatisticsProjection.BuildShinyCaptureDetails(_account, SelectedShinyScopeSeasonId, item.Name, _shinyAvatarResolver);

    internal void ApplyDocument(
        StatisticsDocument document,
        string? selectedUid,
        EncounterSeasonConfig seasonConfig,
        Func<string, BitmapImage?>? avatarResolver = null,
        Func<string, BitmapImage?>? shinyAvatarResolver = null)
    {
        var seasonId = SelectedSeason?.Id;
        var shinySeasonId = SelectedShinyScopeSeasonId;
        var accounts = StatisticsProjection.BuildAccounts(document);
        var account = document.Accounts.FirstOrDefault(item =>
            string.Equals(item.Uid, selectedUid, StringComparison.OrdinalIgnoreCase)) ?? document.Accounts.FirstOrDefault();
        shinyAvatarResolver ??= avatarResolver;
        var seasons = StatisticsProjection.BuildSeasons(account, seasonConfig, avatarResolver, shinyAvatarResolver);
        var scopes = StatisticsProjection.BuildShinyScopes(seasons);
        var shinyCounts = StatisticsProjection.BuildAllShinyCounts(account, shinyAvatarResolver);
        var pendingCaptures = StatisticsProjection.BuildPendingShinyCaptures(account, shinyAvatarResolver);

        // 先替换完整状态，再通知绑定；列表重建期间的 TwoWay 回写不能改变用户的筛选。
        _isRefreshing = true;
        try
        {
            _account = account;
            _shinyAvatarResolver = shinyAvatarResolver;
            Accounts = accounts;
            SelectedAccount = accounts.FirstOrDefault(item => item.Uid == account?.Uid);
            Seasons = seasons;
            ShinyScopes = scopes;
            AllShinyCounts = shinyCounts;
            PendingShinyCaptures = pendingCaptures;
            PendingEncounters = account?.PendingEncounters
                .Where(item => item.HandledAt is null)
                .OrderBy(item => item.DetectedAt)
                .Select(item => new PendingEncounterItem(account.Uid, item.Id, item.RawText, item.Season, item.DetectedAt, item.Name))
                .ToList() ?? [];
            _selectedSeasonIndex = Math.Max(0, FindSeasonIndex(seasons, seasonId));
            _selectedShinyScopeIndex = FindSeasonIndex(seasons, shinySeasonId) + 1;
            PendingEditor.Update(account, LatestPendingShinyCapture);

            OnPropertyChanged(nameof(Accounts));
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(SelectedAccountDisplayName));
            OnPropertyChanged(nameof(Seasons));
            OnPropertyChanged(nameof(ShinyScopes));
            OnPropertyChanged(nameof(AllShinyCounts));
            OnPropertyChanged(nameof(PendingShinyCaptures));
            OnPropertyChanged(nameof(PendingEncounters));
            OnPropertyChanged(nameof(PendingEncounterCount));
            OnPropertyChanged(nameof(PendingEncounterVisibility));
            OnPropertyChanged(nameof(SelectedSeasonIndex));
            OnPropertyChanged(nameof(SelectedShinyScopeIndex));
            OnPropertyChanged(nameof(SelectedSeason));
            OnPropertyChanged(nameof(SelectedShinyScopeSeasonId));
            OnPropertyChanged(nameof(DefaultShinyAddSeasonId));
            OnPropertyChanged(nameof(TotalAllShiny));
            NotifyPendingShinyChanged();
            NotifySelectedShinyChanged();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static int FindSeasonIndex(IReadOnlyList<SeasonStatisticsGroup> seasons, string? id)
    {
        for (var i = 0; i < seasons.Count; i++)
        {
            if (string.Equals(seasons[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private void NotifySelectedShinyChanged()
    {
        OnPropertyChanged(nameof(SelectedShinyCounts));
        OnPropertyChanged(nameof(TotalSelectedShiny));
        OnPropertyChanged(nameof(SelectedShinyDateDisplay));
    }

    private void NotifyPendingShinyChanged()
    {
        OnPropertyChanged(nameof(PendingShinyCount));
        OnPropertyChanged(nameof(LatestPendingShinyCapture));
        OnPropertyChanged(nameof(PendingShinyBadgeVisibility));
        OnPropertyChanged(nameof(PendingShinyConfirmationVisibility));
        OnPropertyChanged(nameof(PendingShinySeasonDisplay));
        OnPropertyChanged(nameof(PendingShinyDetectedAtDisplay));
        OnPropertyChanged(nameof(LatestPendingShinyAvatar));
        OnPropertyChanged(nameof(LatestPendingShinyAvatarVisibility));
        OnPropertyChanged(nameof(LatestPendingShinyAvatarFallbackVisibility));
        OnPropertyChanged(nameof(PendingShinyQueueDisplay));
        OnPropertyChanged(nameof(PendingShinyNameHint));
        OnPropertyChanged(nameof(PendingShinyNameHintVisibility));
    }
}
