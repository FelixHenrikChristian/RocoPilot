using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Models.Logging;
using RocoPilot.Services.Logging;

namespace RocoPilot.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly LogEntryStore _store;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private string _keyword = string.Empty;
    private long _nextSequence;
    private int _syncQueued;
    private bool _isActive;

    [ObservableProperty]
    public partial ObservableCollection<LogEntryItemViewModel> Entries { get; private set; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowInformation { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWarning { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowError { get; set; } = true;

    [ObservableProperty]
    public partial int InformationCount { get; private set; }

    [ObservableProperty]
    public partial int WarningCount { get; private set; }

    [ObservableProperty]
    public partial int ErrorCount { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasEntries { get; private set; }

    public bool IsEmpty => !HasEntries;

    // 列表整体换新后触发，页面据此滚到最新一条。
    public event EventHandler? EntriesReplaced;

    public LogViewModel(LogEntryStore store)
    {
        _store = store;
    }

    // 页面不可见时不跟随新日志，回到页面再整体刷新。
    public void Activate()
    {
        if (_isActive)
        {
            return;
        }

        _isActive = true;
        _store.Changed += Store_Changed;
        Rebuild();
    }

    public void Deactivate()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        _store.Changed -= Store_Changed;
        Entries = [];
    }

    public string FormatVisibleEntries() => LogEntryFormatter.Format(Entries.Select(item => item.Entry));

    partial void OnSearchTextChanged(string value)
    {
        var keyword = (value ?? string.Empty).Trim();
        if (keyword == _keyword)
        {
            return;
        }

        _keyword = keyword;
        Rebuild();
    }

    partial void OnShowInformationChanged(bool value) => Rebuild();

    partial void OnShowWarningChanged(bool value) => Rebuild();

    partial void OnShowErrorChanged(bool value) => Rebuild();

    private void Store_Changed(object? sender, EventArgs e)
    {
        // 连续写入的日志合并成一次界面更新。
        if (Interlocked.Exchange(ref _syncQueued, 1) == 1)
        {
            return;
        }

        if (_uiContext is null)
        {
            Sync();
        }
        else
        {
            _uiContext.Post(static state => ((LogViewModel)state!).Sync(), this);
        }
    }

    private void Sync()
    {
        Volatile.Write(ref _syncQueued, 0);
        if (!_isActive)
        {
            return;
        }

        var batch = _store.ReadFrom(_nextSequence);
        _nextSequence = batch.NextSequence;

        while (Entries.Count > 0 && Entries[0].Entry.Sequence < batch.FirstSequence)
        {
            Entries.RemoveAt(0);
        }

        foreach (var entry in batch.Entries)
        {
            if (Matches(entry))
            {
                Entries.Add(new LogEntryItemViewModel(entry));
            }
        }

        UpdateStatus(batch);
    }

    private void Rebuild()
    {
        var batch = _store.ReadFrom(0);
        _nextSequence = batch.NextSequence;
        Entries = new ObservableCollection<LogEntryItemViewModel>(
            batch.Entries.Where(Matches).Select(entry => new LogEntryItemViewModel(entry)));
        UpdateStatus(batch);
        EntriesReplaced?.Invoke(this, EventArgs.Empty);
    }

    private bool Matches(LogEntry entry)
    {
        var isLevelShown = entry.Severity switch
        {
            LogSeverity.Information => ShowInformation,
            LogSeverity.Warning => ShowWarning,
            _ => ShowError
        };

        return isLevelShown
            && (_keyword.Length == 0
                || entry.Message.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                || entry.Source.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                || entry.Exception?.Contains(_keyword, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void UpdateStatus(LogEntryBatch batch)
    {
        InformationCount = batch.Counts.Information;
        WarningCount = batch.Counts.Warning;
        ErrorCount = batch.Counts.Error;

        var total = batch.Counts.Total;
        var status = Entries.Count == total ? $"共 {total} 条" : $"显示 {Entries.Count} / {total} 条";
        StatusText = batch.FirstSequence > 0 ? $"{status}，更早的记录在日志文件中" : status;
        EmptyText = total == 0 ? "本次运行还没有日志" : "没有符合条件的日志";
        HasEntries = Entries.Count > 0;
    }
}
