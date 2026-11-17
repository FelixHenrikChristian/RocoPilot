using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;

using RocoPilot.Services.Logging;
using RocoPilot.ViewModels;

using Serilog;

using Windows.ApplicationModel.DataTransfer;

namespace RocoPilot.Views;

public sealed partial class LogPage : Page
{
    public LogViewModel ViewModel { get; } = App.GetService<LogViewModel>();

    public LogPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.EntriesReplaced -= ViewModel_EntriesReplaced;
        ViewModel.EntriesReplaced += ViewModel_EntriesReplaced;
        ViewModel.Activate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.EntriesReplaced -= ViewModel_EntriesReplaced;
        ViewModel.Deactivate();
    }

    private void ViewModel_EntriesReplaced(object? sender, EventArgs e)
    {
        // 等换新的列表完成布局后再滚到最新一条。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var entries = ViewModel.Entries;
            if (entries.Count > 0)
            {
                LogList.ScrollIntoView(entries[^1]);
            }
        });
    }

    private void LogList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LogEntryItemViewModel item)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void LogEntry_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LogEntryItemViewModel item } target)
        {
            return;
        }

        e.Handled = true;
        var copy = new MenuFlyoutItem { Text = "复制这条日志" };
        copy.Click += (_, _) => CopyToClipboard(LogEntryFormatter.Format(item.Entry), "已复制这条日志。");

        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        menu.ShowAt(target, e.GetPosition(target));
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.Entries.Count;
        CopyToClipboard(ViewModel.FormatVisibleEntries(), $"已复制 {count} 条日志。");
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        // 先取文本，保存窗口打开期间新增的日志不混进这次导出。
        var count = ViewModel.Entries.Count;
        var text = ViewModel.FormatVisibleEntries();

        string? filePath;
        try
        {
            var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
            {
                Title = "导出日志",
                SuggestedStartFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                SuggestedFileName = $"RocoPilot_Log_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log",
                DefaultFileExtension = ".log",
                FileTypeChoices = { { "日志文件", new List<string> { ".log" } } }
            };
            filePath = (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(LogPage)).Error(ex, "导出日志的保存窗口打不开");
            ShowNotice(InfoBarSeverity.Error, "打开导出窗口失败", ex.Message);
            return;
        }

        if (filePath is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(filePath, text);
            Log.ForContext(typeof(LogPage)).Information("已导出 {Count} 条日志：{Path}", count, filePath);
            ShowNotice(InfoBarSeverity.Success, "导出完成", filePath);
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(LogPage)).Error(ex, "导出日志失败：{Path}", filePath);
            ShowNotice(InfoBarSeverity.Error, "导出失败", ex.Message);
        }
    }

    private void CopyToClipboard(string text, string message)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            // 关闭应用后剪贴板内容仍可粘贴。
            Clipboard.Flush();
            ShowNotice(InfoBarSeverity.Success, "已复制", message);
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(LogPage)).Error(ex, "复制日志失败");
            ShowNotice(InfoBarSeverity.Error, "复制失败", ex.Message);
        }
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        LogNotice.IsOpen = false;
        LogNotice.Severity = severity;
        LogNotice.Title = title;
        LogNotice.Message = message;
        LogNotice.IsOpen = true;
    }
}
