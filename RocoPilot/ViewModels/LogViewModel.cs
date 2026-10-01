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

        Entries.Clear();
        foreach (var entry in _logBuffer.Snapshot())
        {
            if (PassesFilter(entry))
            {
                Entries.Add(entry);
            }
        }

        if (!_subscribed)
        {
            _logBuffer.EntryWritten += OnEntryWritten;
            _subscribed = true;
        }
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

    private void OnEntryWritten(LogEntry entry)
    {
        var enqueue = _enqueue;
        if (enqueue == null)
        {
            return;
        }

        enqueue(() =>
        {
            // 清空前已排队或延迟送达的通知不能重新加入列表。
            if (!_logBuffer.IsCurrent(entry) || !PassesFilter(entry))
            {
                return;
            }

            Entries.Add(entry);
            while (Entries.Count > MaxDisplayEntries)
            {
                Entries.RemoveAt(0);
            }
        });
    }

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

    partial void OnShowDebugChanged(bool value) => RebuildFromBuffer();
    partial void OnShowInformationChanged(bool value) => RebuildFromBuffer();
    partial void OnShowWarningChanged(bool value) => RebuildFromBuffer();
    partial void OnShowErrorChanged(bool value) => RebuildFromBuffer();
    partial void OnSearchTextChanged(string value) => RebuildFromBuffer();

    private void RebuildFromBuffer()
    {
        var enqueue = _enqueue;
        if (enqueue == null)
        {
            return;
        }

        enqueue(() =>
        {
            Entries.Clear();
            foreach (var entry in _logBuffer.Snapshot())
            {
                if (PassesFilter(entry))
                {
                    Entries.Add(entry);
                    if (Entries.Count >= MaxDisplayEntries)
                    {
                        break;
                    }
                }
            }
        });
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
