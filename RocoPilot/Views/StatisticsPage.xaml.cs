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

    private async void PollutionCountItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target
            || target.DataContext is not SpiritCountItem item
            || string.IsNullOrWhiteSpace(item.Season))
        {
            return;
        }

        await ShowEncounterDetailsAsync(item.Season, item);
        e.Handled = true;
    }

    private async void ShinyCountItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target || target.DataContext is not SpiritCountItem item)
        {
            return;
        }

        await ShowShinyDetailsAsync(item);
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
        var input = await StatisticsEntryDialogs.ShowStatisticEntryAsync(XamlRoot, "新增奇遇条目", "新增");
        if (input is null)
        {
            return;
        }

        await ViewModel.AddEncounterAsync(seasonId, input.Name, input.Count);
    }

    private async Task EditEncounterAsync(string seasonId, SpiritCountItem item)
    {
        var input = await StatisticsEntryDialogs.ShowStatisticEntryAsync(
            XamlRoot,
            "编辑奇遇条目",
            "保存",
            item.Name,
            item.Count);
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

    private async Task ShowEncounterDetailsAsync(string seasonId, SpiritCountItem item)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var action = StatisticDetailAction.None;
        ContentDialog? dialog = null;
        var editButton = new Button { Content = "编辑" };
        var deleteButton = new Button
        {
            Content = "删除",
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C))
        };
        editButton.Click += (_, _) =>
        {
            action = StatisticDetailAction.Edit;
            dialog?.Hide();
        };
        deleteButton.Click += (_, _) =>
        {
            action = StatisticDetailAction.Delete;
            dialog?.Hide();
        };

        var actions = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                editButton,
                deleteButton
            }
        };
        var content = new StackPanel
        {
            Width = 340,
            Spacing = 16,
            Children =
            {
                CreateDetailHeader(item.Name),
                CreateDetailRow("奇遇计数", $"{item.Count} 次"),
                CreateDetailRow("最近记录", item.LastCapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                actions
            }
        };

        dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "奇遇详情",
            Content = content,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();

        if (action == StatisticDetailAction.Edit)
        {
            await EditEncounterAsync(seasonId, item);
        }
        else if (action == StatisticDetailAction.Delete)
        {
            await DeleteEncounterAsync(seasonId, item);
        }
    }

    private async Task AddShinyAsync()
    {
        var input = await StatisticsEntryDialogs.ShowShinyEntryAsync(XamlRoot);
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
            Width = 470,
            Height = 226,
            Background = null,
            ItemTemplate = Resources["ShinyCaptureDetailTemplate"] as DataTemplate,
            ItemsSource = details,
            SelectedIndex = 0
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

        ShinyCaptureDetailItem? GetSelectedDetail()
        {
            return flipView.SelectedItem as ShinyCaptureDetailItem
                ?? details.ElementAtOrDefault(Math.Max(0, flipView.SelectedIndex));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "异色详情",
            Content = flipView,
            PrimaryButtonText = "编辑",
            SecondaryButtonText = "删除",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();

        var selectedDetail = GetSelectedDetail();
        if (selectedDetail is null)
        {
            return;
        }

        if (result == ContentDialogResult.Primary)
        {
            await EditShinyCaptureAsync(selectedDetail);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await DeleteShinyCaptureAsync(selectedDetail);
        }
    }

    private async Task EditShinyCaptureAsync(ShinyCaptureDetailItem item)
    {
        var input = await StatisticsEntryDialogs.ShowShinyCaptureEditAsync(XamlRoot, item);
        if (input is null)
        {
            return;
        }

        await ViewModel.EditShinyCaptureAsync(
            item,
            input.Name,
            input.EncounterCountBeforeCapture,
            input.CapturedAt);
    }

    private async Task DeleteShinyCaptureAsync(ShinyCaptureDetailItem item)
    {
        if (await ConfirmDeleteStatisticItemAsync(
                "删除异色记录",
                $"将删除 {item.Name} 在 {item.CapturedDateDisplay} {item.CapturedTimeDisplay} 获取的异色记录。是否继续？"))
        {
            await ViewModel.DeleteShinyCaptureAsync(item);
        }
    }

    private static TextBlock CreateDetailHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
    }

    private static Grid CreateDetailRow(string label, string value)
    {
        var row = new Grid
        {
            MinHeight = 34,
            ColumnSpacing = 14
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelTextBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        var valueTextBlock = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        Grid.SetColumn(labelTextBlock, 0);
        Grid.SetColumn(valueTextBlock, 1);
        row.Children.Add(labelTextBlock);
        row.Children.Add(valueTextBlock);
        return row;
    }

    private async void ConfirmPendingShinyButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConfirmLatestPendingShinyAsync();
    }

    private async void DiscardPendingShinyButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DiscardLatestPendingShinyAsync();
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

    private enum StatisticDetailAction
    {
        None,
        Edit,
        Delete
    }
}
