using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

namespace RocoPilot.ViewModels;

public partial class DailyViewModel : ObservableObject
{
    private readonly IIndependentTaskService _tasks;
    private readonly ILocalSettingsService _settings;
    private readonly ILogger<DailyViewModel> _logger;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _loaded;

    public ObservableCollection<DailyTaskItemViewModel> Items { get; } = [];

    public DailyTaskItemViewModel? FlowerSeedTask => Items.FirstOrDefault(item => item.Kind == DailyTaskKind.FlowerSeedChallenge);
    public DailyTaskItemViewModel? LegendaryTask => Items.FirstOrDefault(item => item.Kind == DailyTaskKind.LegendaryChallenge);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyPropertyChangedFor(nameof(StartButtonGlyph))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool CanStartOrStop { get; set; }

    [ObservableProperty]
    public partial bool CanReorder { get; set; }

    [ObservableProperty]
    public partial string? SelectedLegendaryTarget { get; set; }

    [ObservableProperty]
    public partial int SelectedLegendaryDifficultyIndex { get; set; }

    [ObservableProperty]
    public partial double LegendaryChallengeRunCount { get; set; }

    [ObservableProperty]
    public partial FlowerSeedOption? SelectedFlowerSeed { get; set; }

    [ObservableProperty]
    public partial double FlowerSeedRunCount { get; set; }

    public event EventHandler<string>? NoticeRequested;

    public string StartButtonText => IsRunning ? "停止任务" : "开始每日";
    public string StartButtonGlyph => IsRunning ? "\uF2D9" : "\uE768";

    public DailyViewModel(IIndependentTaskService tasks, ILocalSettingsService settings, ILogger<DailyViewModel> logger)
    {
        _tasks = tasks;
        _settings = settings;
        _logger = logger;
        _tasks.StateChanged += Tasks_StateChanged;
        Items.CollectionChanged += Items_CollectionChanged;
    }

    public async Task LoadAsync()
    {
        await _tasks.LoadSettingsAsync();
        if (!_loaded)
        {
            DailyTaskSettings? saved = null;
            try
            {
                saved = await _settings.ReadSettingAsync<DailyTaskSettings>(SettingsKeys.DailyTaskSettings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取每日清单失败，使用默认清单。");
                NoticeRequested?.Invoke(this, "读取每日清单失败，已使用默认清单。");
            }

            Apply(saved ?? new(), _tasks.Settings);
        }

        Refresh();
    }

    private void Apply(DailyTaskSettings settings, IndependentTaskSettings defaults)
    {
        _loaded = false;
        foreach (var item in Items) item.PropertyChanged -= Item_PropertyChanged;
        Items.Clear();
        foreach (var entry in settings.Normalize().Tasks)
        {
            var item = new DailyTaskItemViewModel(entry.Kind);
            item.IsEnabled = item.IsAvailable && entry.IsEnabled;
            item.PropertyChanged += Item_PropertyChanged;
            Items.Add(item);
        }

        var challenges = (settings.Challenges ?? defaults).Normalize(_tasks.LegendaryTargets);
        SelectedLegendaryTarget = challenges.LegendaryTargetName;
        SelectedLegendaryDifficultyIndex = challenges.LegendaryDifficulty - 1;
        LegendaryChallengeRunCount = challenges.LegendaryChallengeRunCount;
        SelectedFlowerSeed = _tasks.Settings.FlowerSeedOptions.FirstOrDefault(o => o.Number == challenges.FlowerSeedTargetNumber);
        FlowerSeedRunCount = challenges.FlowerSeedRunCount;
        OnPropertyChanged(nameof(FlowerSeedTask));
        OnPropertyChanged(nameof(LegendaryTask));
        _loaded = true;
    }

    private bool CanReset() => _loaded && !_tasks.IsRunning;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        Apply(new(), IndependentTaskSettings.CreateDefault());
        Refresh();
        await SaveAsync();
    }

    private IndependentTaskSettings ChallengeSnapshot() => new()
    {
        LegendaryTargetName = SelectedLegendaryTarget ?? string.Empty,
        LegendaryDifficulty = SelectedLegendaryDifficultyIndex + 1,
        LegendaryChallengeRunCount = TasksViewModel.ToRunCount(LegendaryChallengeRunCount, IndependentTaskSettings.DefaultLegendaryChallengeRunCount),
        FlowerSeedTargetNumber = SelectedFlowerSeed?.Number ?? 0,
        FlowerSeedRunCount = TasksViewModel.ToRunCount(FlowerSeedRunCount, IndependentTaskSettings.DefaultFlowerSeedRunCount)
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loaded && e.PropertyName is nameof(SelectedLegendaryTarget) or nameof(SelectedLegendaryDifficultyIndex)
            or nameof(LegendaryChallengeRunCount) or nameof(SelectedFlowerSeed) or nameof(FlowerSeedRunCount))
            _ = SaveAsync();
    }

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (_tasks.IsDailyRunning)
        {
            await _tasks.StopAsync();
        }
        else
        {
            var kinds = Items.Where(item => item.IsEnabled && item.IsAvailable)
                .Select(item => item.TaskKind!.Value).ToArray();
            var result = await _tasks.StartDailyAsync(kinds, ChallengeSnapshot());
            if (!result.Success) NoticeRequested?.Invoke(this, result.Message);
        }

        Refresh();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ListView 原生拖动排序先移除再插入；清单成员固定，加载后的插入即代表一次排序完成。
        if (!_loaded || e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move)) return;
        Refresh();
        _ = SaveAsync();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DailyTaskItemViewModel.IsEnabled)) return;
        Refresh();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        var snapshot = new DailyTaskSettings
        {
            Challenges = ChallengeSnapshot(),
            Tasks = Items.Select(item => new DailyTaskEntry(item.Kind, item.IsEnabled)).ToList()
        };
        await _saveLock.WaitAsync();
        try
        {
            await _settings.SaveSettingAsync(SettingsKeys.DailyTaskSettings, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存每日清单失败。");
            NoticeRequested?.Invoke(this, "每日清单保存失败，重新启动后可能丢失此次修改。");
        }
        finally { _saveLock.Release(); }
    }

    private void Tasks_StateChanged(object? sender, EventArgs e)
    {
        if (_uiContext is null || _uiContext == SynchronizationContext.Current) Refresh();
        else _uiContext.Post(_ => Refresh(), null);
    }

    private void Refresh()
    {
        ResetCommand.NotifyCanExecuteChanged();
        IsRunning = _tasks.IsDailyRunning;
        var enabled = Items.Count(item => item.IsEnabled && item.IsAvailable);
        CanStartOrStop = _loaded && (IsRunning || !_tasks.IsRunning && enabled > 0);
        CanReorder = _loaded && !_tasks.IsRunning;
        foreach (var item in Items)
        {
            item.CanToggle = item.IsAvailable && !_tasks.IsRunning;
            var isExecuting = IsRunning && _tasks.RunningTaskKind == item.TaskKind;
            item.Status = !item.IsAvailable ? "待接入" : isExecuting ? "执行中" : item.IsEnabled ? "已启用" : "未启用";
        }
    }
}
