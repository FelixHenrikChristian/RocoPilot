using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using RocoPilot.ViewModels;

using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

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

    private void AccountListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AccountStatisticsOption account)
        {
            return;
        }

        ViewModel.SelectedAccount = account;
        AccountSelectorFlyout.Hide();
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var uidTextBox = new TextBox
        {
            Header = "UID",
            MaxLength = 32,
            PlaceholderText = "请输入 UID"
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "添加账号",
            Content = uidTextBox,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (await ViewModel.AddAccountAsync(uidTextBox.Text))
        {
            AccountSelectorFlyout.Hide();
        }
    }

    private void AccountItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target
            || target.DataContext is not AccountStatisticsOption account)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除账号"
        };
        deleteItem.Click += async (_, _) => await ConfirmDeleteAccountAsync(account);

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(target);
        e.Handled = true;
    }

    private async Task ConfirmDeleteAccountAsync(AccountStatisticsOption account)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "删除账号",
            Content = $"将删除账号 {account.Uid} 及其所有统计记录。此操作不会删除导出的备份文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAccountAsync(account.Uid);
            AccountSelectorFlyout.Hide();
        }
    }

    private void PollutionCountItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target
            || target.DataContext is not SpiritCountItem item
            || string.IsNullOrWhiteSpace(item.Season))
        {
            return;
        }

        var flyout = CreateStatisticItemMenu(
            editHandler: async () => await EditEncounterAsync(item.Season, item),
            deleteHandler: async () => await DeleteEncounterAsync(item.Season, item));
        flyout.ShowAt(target);
        e.Handled = true;
    }

    private void ShinyCountItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target || target.DataContext is not SpiritCountItem item)
        {
            return;
        }

        var flyout = CreateStatisticItemMenu(
            editHandler: async () => await EditShinyAsync(item),
            deleteHandler: async () => await DeleteShinyAsync(item),
            detailHandler: async () => await ShowShinyDetailsAsync(item));
        flyout.ShowAt(target);
        e.Handled = true;
    }

    private void PollutionBlank_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target || target.DataContext is not SeasonStatisticsGroup season)
        {
            return;
        }

        var flyout = CreateAddStatisticMenu(async () => await AddEncounterAsync(season.Id));
        flyout.ShowAt(target);
        e.Handled = true;
    }

    private void ShinyBlank_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        var flyout = CreateAddStatisticMenu(async () => await AddShinyAsync());
        flyout.ShowAt(target);
        e.Handled = true;
    }

    private static MenuFlyout CreateStatisticItemMenu(
        Func<Task> editHandler,
        Func<Task> deleteHandler,
        Func<Task>? detailHandler = null)
    {
        var flyout = new MenuFlyout();
        if (detailHandler is not null)
        {
            var detailItem = new MenuFlyoutItem
            {
                Text = "详情"
            };
            detailItem.Click += async (_, _) => await detailHandler();
            flyout.Items.Add(detailItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var editItem = new MenuFlyoutItem
        {
            Text = "编辑"
        };
        editItem.Click += async (_, _) => await editHandler();

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除",
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C))
        };
        deleteItem.Click += async (_, _) => await deleteHandler();

        flyout.Items.Add(editItem);
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private static MenuFlyout CreateAddStatisticMenu(Func<Task> addHandler)
    {
        var addItem = new MenuFlyoutItem
        {
            Text = "新增条目"
        };
        addItem.Click += async (_, _) => await addHandler();

        var flyout = new MenuFlyout();
        flyout.Items.Add(addItem);
        return flyout;
    }

    private async Task AddEncounterAsync(string seasonId)
    {
        var input = await ShowStatisticEntryDialogAsync("新增奇遇条目", "新增");
        if (input is null)
        {
            return;
        }

        await ViewModel.AddEncounterAsync(seasonId, input.Name, input.Count);
    }

    private async Task EditEncounterAsync(string seasonId, SpiritCountItem item)
    {
        var input = await ShowStatisticEntryDialogAsync("编辑奇遇条目", "保存", item.Name, item.Count);
        if (input is null)
        {
            return;
        }

        await ViewModel.EditEncounterAsync(seasonId, item, input.Name, input.Count);
    }

    private async Task DeleteEncounterAsync(string seasonId, SpiritCountItem item)
    {
        if (await ConfirmDeleteStatisticItemAsync("删除奇遇条目", $"将删除 {item.Name} 的奇遇统计记录。是否继续？"))
        {
            await ViewModel.DeleteEncounterAsync(seasonId, item);
        }
    }

    private async Task AddShinyAsync()
    {
        var input = await ShowShinyEntryDialogAsync();
        if (input is null)
        {
            return;
        }

        await ViewModel.AddShinyAsync(
            ViewModel.DefaultShinyAddSeasonId,
            input.Name,
            input.Count,
            input.CapturedAt,
            input.ResetEncounterCount,
            input.EncounterCountBeforeCapture);
    }

    private async Task EditShinyAsync(SpiritCountItem item)
    {
        var input = await ShowStatisticEntryDialogAsync("编辑异色条目", "保存", item.Name, item.Count);
        if (input is null)
        {
            return;
        }

        await ViewModel.EditShinyAsync(item, input.Name, input.Count);
    }

    private async Task DeleteShinyAsync(SpiritCountItem item)
    {
        if (await ConfirmDeleteStatisticItemAsync("删除异色条目", $"将删除 {item.Name} 的异色统计记录。是否继续？"))
        {
            await ViewModel.DeleteShinyAsync(item);
        }
    }

    private async Task ShowShinyDetailsAsync(SpiritCountItem item)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var details = ViewModel.GetShinyCaptureDetails(item).ToList();
        if (details.Count == 0)
        {
            return;
        }

        var flipView = new FlipView
        {
            Width = 460,
            MinHeight = 260,
            Background = null,
            ItemTemplate = Resources["ShinyCaptureDetailTemplate"] as DataTemplate,
            ItemsSource = details
        };

        flipView.PointerWheelChanged += (_, args) =>
        {
            if (details.Count < 2)
            {
                return;
            }

            var wheelDelta = args.GetCurrentPoint(flipView).Properties.MouseWheelDelta;
            var direction = wheelDelta > 0 ? -1 : 1;
            flipView.SelectedIndex = (flipView.SelectedIndex + direction + details.Count) % details.Count;
            args.Handled = true;
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "异色详情",
            Content = flipView,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private async void ConfirmPendingShinyButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConfirmLatestPendingShinyAsync();
    }

    private async void DiscardPendingShinyButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DiscardLatestPendingShinyAsync();
    }

    private async Task<StatisticEntryEditResult?> ShowStatisticEntryDialogAsync(
        string title,
        string primaryButtonText,
        string name = "",
        int count = 1)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return null;
        }

        var nameTextBox = new TextBox
        {
            Header = "精灵名",
            MaxLength = 32,
            PlaceholderText = "仅支持中文、英文和短横线",
            Text = name
        };
        var countNumberBox = new NumberBox
        {
            Header = "计数",
            Minimum = 1,
            Value = Math.Max(1, count),
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                nameTextBox,
                countNumberBox
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var nextCount = double.IsNaN(countNumberBox.Value)
            ? 0
            : (int)Math.Round(countNumberBox.Value);
        return new StatisticEntryEditResult(nameTextBox.Text, nextCount);
    }

    private async Task<ShinyEntryAddResult?> ShowShinyEntryDialogAsync()
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var nameTextBox = new TextBox
        {
            Header = "精灵名",
            MaxLength = 32,
            PlaceholderText = "仅支持中文、英文和短横线"
        };
        var countNumberBox = new NumberBox
        {
            Header = "异色计数",
            Minimum = 1,
            Value = 1,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var addModeComboBox = new ComboBox
        {
            Header = "新增类型",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                "本次漏识别（清空当前奇遇）",
                "历史补录（保留当前奇遇）"
            }
        };
        addModeComboBox.SelectedIndex = 0;
        var capturedDatePicker = new CalendarDatePicker
        {
            Header = "捕获日期",
            Date = now,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var capturedTimePicker = new TimePicker
        {
            Header = "捕获时间",
            Time = now.TimeOfDay,
            MinuteIncrement = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var encounterCountNumberBox = new NumberBox
        {
            Header = "异色前奇遇",
            Minimum = 0,
            Value = 0,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var capturedTimePanel = new StackPanel
        {
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Children =
            {
                capturedDatePicker,
                capturedTimePicker,
                encounterCountNumberBox
            }
        };

        addModeComboBox.SelectionChanged += (_, _) =>
        {
            capturedTimePanel.Visibility = addModeComboBox.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                nameTextBox,
                countNumberBox,
                addModeComboBox,
                capturedTimePanel
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "新增异色条目",
            Content = content,
            PrimaryButtonText = "新增",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var nextCount = double.IsNaN(countNumberBox.Value)
            ? 0
            : (int)Math.Round(countNumberBox.Value);
        var resetEncounterCount = addModeComboBox.SelectedIndex != 1;
        var capturedAt = resetEncounterCount
            ? DateTimeOffset.Now
            : ResolveHistoricalCapturedAt(capturedDatePicker, capturedTimePicker, now);
        var encounterCountBeforeCapture = double.IsNaN(encounterCountNumberBox.Value)
            ? 0
            : Math.Max(0, (int)Math.Round(encounterCountNumberBox.Value));

        return new ShinyEntryAddResult(
            nameTextBox.Text,
            nextCount,
            capturedAt,
            resetEncounterCount,
            resetEncounterCount ? null : encounterCountBeforeCapture);
    }

    private static DateTimeOffset ResolveHistoricalCapturedAt(
        CalendarDatePicker capturedDatePicker,
        TimePicker capturedTimePicker,
        DateTimeOffset fallback)
    {
        var selectedDate = capturedDatePicker.Date ?? fallback;
        var localDate = selectedDate.LocalDateTime.Date;
        return new DateTimeOffset(localDate + capturedTimePicker.Time, fallback.Offset);
    }

    private async Task<bool> ConfirmDeleteStatisticItemAsync(string title, string content)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

    private sealed record StatisticEntryEditResult(string Name, int Count);

    private sealed record ShinyEntryAddResult(
        string Name,
        int Count,
        DateTimeOffset CapturedAt,
        bool ResetEncounterCount,
        int? EncounterCountBeforeCapture);
}
