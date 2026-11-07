using System.Collections.ObjectModel;
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
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
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

    public event EventHandler<string>? NoticeRequested;

    public string StartButtonText => IsRunning ? "停止任务" : "开始每日";
    public string StartButtonGlyph => IsRunning ? "\uF2D9" : "\uE768";

    public DailyViewModel(IIndependentTaskService tasks, ILocalSettingsService settings, ILogger<DailyViewModel> logger)
    {
        _tasks = tasks;
        _settings = settings;
        _logger = logger;
        _tasks.StateChanged += Tasks_StateChanged;
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

            foreach (var entry in (saved ?? new()).Normalize().Tasks)
            {
                var item = new DailyTaskItemViewModel(entry.Kind);
                item.IsEnabled = item.IsAvailable && entry.IsEnabled;
                item.PropertyChanged += Item_PropertyChanged;
                Items.Add(item);
            }

            OnPropertyChanged(nameof(FlowerSeedTask));
            OnPropertyChanged(nameof(LegendaryTask));
            _loaded = true;
        }

        Refresh();
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
            var result = await _tasks.StartDailyAsync(kinds);
            if (!result.Success) NoticeRequested?.Invoke(this, result.Message);
        }

        Refresh();
    }

    internal void Move(DailyTaskItemViewModel? item, int offset)
    {
        if (item is null || _tasks.IsRunning) return;
        var index = Items.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Items.Count) return;
        Items.Move(index, target);
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
        if (_dispatcher is null || _dispatcher.HasThreadAccess) Refresh();
        else _dispatcher.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        IsRunning = _tasks.IsDailyRunning;
        var enabled = Items.Count(item => item.IsEnabled && item.IsAvailable);
        CanStartOrStop = _loaded && (IsRunning || !_tasks.IsRunning && enabled > 0);
        foreach (var item in Items)
        {
            item.CanToggle = item.IsAvailable && !_tasks.IsRunning;
            var isExecuting = IsRunning && _tasks.RunningTaskKind == item.TaskKind;
            item.Status = !item.IsAvailable ? "待接入" : isExecuting ? "执行中" : item.IsEnabled ? "已启用" : "未启用";
        }
    }
}
