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

using Serilog.Events;

namespace RocoPilot.ViewModels;

public partial class LogViewModel : ObservableRecipient
{
    private const int MaxDisplayEntries = 2000;

    private readonly ILogger<LogViewModel> _logger;

    private DispatcherQueue? _dispatcher;
    private bool _subscribed;

    public LogViewModel(ILogger<LogViewModel> logger)
    {
        _logger = logger;
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
    {
        _dispatcher = dispatcher;

        Entries.Clear();
        foreach (var entry in LoggingHelper.LogBuffer.Snapshot())
        {
            if (PassesFilter(entry))
            {
                Entries.Add(entry);
            }
        }

        if (!_subscribed)
        {
            LoggingHelper.LogBuffer.EntryWritten += OnEntryWritten;
            _subscribed = true;
        }
    }

    public void Detach()
    {
        if (_subscribed)
        {
            LoggingHelper.LogBuffer.EntryWritten -= OnEntryWritten;
            _subscribed = false;
        }

        _dispatcher = null;
    }

    private void OnEntryWritten(LogEntry entry)
    {
        var dq = _dispatcher;
        if (dq == null)
        {
            return;
        }

        dq.TryEnqueue(() =>
        {
            if (!PassesFilter(entry))
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
        var dq = _dispatcher;
        if (dq == null)
        {
            return;
        }

        dq.TryEnqueue(() =>
        {
            Entries.Clear();
            foreach (var entry in LoggingHelper.LogBuffer.Snapshot())
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
