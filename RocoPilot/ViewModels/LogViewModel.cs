using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Services.Logging;

using Serilog.Events;

namespace RocoPilot.ViewModels;

public partial class LogViewModel : ObservableRecipient
{
    private const int MaxDisplayEntries = 2000;

    private readonly ILogger<LogViewModel> _logger;
    private readonly InMemoryLogSink _logBuffer;

    private Action<Action>? _enqueue;
    private bool _subscribed;
    private int _refreshQueued;

    public LogViewModel(ILogger<LogViewModel> logger)
        : this(logger, LoggingHelper.LogBuffer)
    {
    }

    internal LogViewModel(ILogger<LogViewModel> logger, InMemoryLogSink logBuffer)
    {
        _logger = logger;
        _logBuffer = logBuffer;
        ShowInformation = true;
        ShowWarning = true;
        ShowError = true;
        SearchText = string.Empty;
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty]
    public partial bool ShowDebug { get; set; }

    [ObservableProperty]
    public partial bool ShowInformation { get; set; }

    [ObservableProperty]
    public partial bool ShowWarning { get; set; }

    [ObservableProperty]
    public partial bool ShowError { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    public void Attach(DispatcherQueue dispatcher)
        => Attach(callback => dispatcher.TryEnqueue(() => callback()));

    internal void Attach(Action<Action> enqueue)
    {
        _enqueue = enqueue;

        if (!_subscribed)
        {
            _logBuffer.EntryWritten += OnEntryWritten;
            _subscribed = true;
        }
        RefreshEntries();
    }

    public void Detach()
    {
        if (_subscribed)
        {
            _logBuffer.EntryWritten -= OnEntryWritten;
            _subscribed = false;
        }

        _enqueue = null;
    }

    private void OnEntryWritten(LogEntry entry) => RequestRefresh();

    private bool PassesFilter(LogEntry entry)
    {
        if (!IsLevelEnabled(entry.Level))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText;
            if (entry.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                entry.SourceContext.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsLevelEnabled(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => ShowDebug,
        LogEventLevel.Debug => ShowDebug,
        LogEventLevel.Information => ShowInformation,
        LogEventLevel.Warning => ShowWarning,
        LogEventLevel.Error => ShowError,
        LogEventLevel.Fatal => ShowError,
        _ => true,
    };

    partial void OnShowDebugChanged(bool value) => RequestRefresh();
    partial void OnShowInformationChanged(bool value) => RequestRefresh();
    partial void OnShowWarningChanged(bool value) => RequestRefresh();
    partial void OnShowErrorChanged(bool value) => RequestRefresh();
    partial void OnSearchTextChanged(string value) => RequestRefresh();

    private void RequestRefresh()
    {
        var enqueue = _enqueue;
        if (enqueue is null || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;

        enqueue(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            if (_enqueue is not null) RefreshEntries();
        });
    }

    private void RefreshEntries()
    {
        // 通知只请求刷新，始终消费当前缓存，避免筛选、清空和重新进入时重复追加旧事件。
        var snapshot = _logBuffer.Snapshot().Where(PassesFilter).TakeLast(MaxDisplayEntries).ToArray();
        var removedPrefix = snapshot.Length == 0 ? -1 : Entries.IndexOf(snapshot[0]);
        if (removedPrefix < 0) Entries.Clear();
        else while (removedPrefix-- > 0) Entries.RemoveAt(0);

        // 普通新日志只追加，保留已有行及滚动位置；筛选变化时替换不同的部分。
        var shared = 0;
        while (shared < Entries.Count && shared < snapshot.Length && ReferenceEquals(Entries[shared], snapshot[shared]))
            shared++;
        while (Entries.Count > shared) Entries.RemoveAt(Entries.Count - 1);
        foreach (var entry in snapshot.Skip(shared)) Entries.Add(entry);
    }

    [RelayCommand]
    private void Clear()
    {
        _logBuffer.Clear();
        Entries.Clear();
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        try
        {
            Directory.CreateDirectory(LoggingHelper.LogDirectory);

            var todayFile = Path.Combine(
                LoggingHelper.LogDirectory,
                $"app-{DateTime.Now:yyyyMMdd}.log");

            var target = File.Exists(todayFile) ? todayFile : LoggingHelper.LogDirectory;

            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开日志文件失败");
        }
    }
}
