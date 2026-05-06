using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;

using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace RocoPilot.Views;

public sealed partial class StatisticsPage : Page
{
    private static readonly TimeSpan ScrollBarHideDelay = TimeSpan.FromMilliseconds(700);

    private readonly Dictionary<ScrollViewer, DispatcherTimer> _scrollBarHideTimers = [];

    public StatisticsViewModel ViewModel
    {
        get;
    }

    public StatisticsPage()
    {
        ViewModel = App.GetService<StatisticsViewModel>();
        InitializeComponent();
        Loaded += StatisticsPage_Loaded;
    }

    private async void StatisticsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void ImportStatisticsMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".json");

        InitializePicker(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var json = await FileIO.ReadTextAsync(file, UnicodeEncoding.Utf8);
            await ViewModel.ImportFromJsonAsync(json);
        }
        catch (Exception ex)
        {
            ViewModel.ShowOperationFailed("导入失败", ex);
        }
    }

    private async void ExportStatisticsMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"RocoPilot_Statistics_{DateTimeOffset.Now:yyyyMMdd_HHmmss}"
        };
        picker.FileTypeChoices.Add("JSON 文件", new List<string> { ".json" });

        InitializePicker(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await FileIO.WriteTextAsync(file, ViewModel.ExportToJson(), UnicodeEncoding.Utf8);
            ViewModel.ShowExported(file.Path);
        }
        catch (Exception ex)
        {
            ViewModel.ShowOperationFailed("导出失败", ex);
        }
    }

    private async void ClearStatisticsMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "清空记录",
            Content = "将清空所有账号和统计记录。此操作不会删除导出的备份文件。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearAllAsync();
        }
    }

    private static void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private void CardScrollViewer_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        RestartScrollBarHideTimer(scrollViewer);
    }

    private void CardScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        RestartScrollBarHideTimer(scrollViewer);
    }

    private void RestartScrollBarHideTimer(ScrollViewer scrollViewer)
    {
        if (!_scrollBarHideTimers.TryGetValue(scrollViewer, out var timer))
        {
            timer = new DispatcherTimer { Interval = ScrollBarHideDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            };
            _scrollBarHideTimers[scrollViewer] = timer;
        }

        timer.Stop();
        timer.Start();
    }
}
