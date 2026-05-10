using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

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
    private ContentDialog? _activeShinyDetailDialog;
    private StatisticDetailAction _requestedShinyDetailAction = StatisticDetailAction.None;
    private ShinyCaptureDetailItem? _requestedShinyDetailItem;

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
        if (await ConfirmDeleteEncounterAsync(seasonId, item))
        {
            await ViewModel.DeleteEncounterAsync(seasonId, item);
        }
    }

    private async Task<bool> ConfirmDeleteEncounterAsync(string seasonId, SpiritCountItem item)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return false;
        }

        var content = new StackPanel
        {
            Width = 420,
            Spacing = 14,
            Children =
            {
                CreateDialogHeaderCard(
                    "\uE74D",
                    item.Name,
                    $"{FormatSeasonDisplay(seasonId)} · 奇遇统计",
                    "该操作会删除当前赛季中这个精灵的奇遇计数。",
                    new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
                    new SolidColorBrush(Color.FromArgb(0x1A, 0xC4, 0x2B, 0x1C)),
                    item.Avatar),
            }
        };

        var detailGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10
        };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var countTile = CreateDialogInfoTile("\uE81D", "奇遇计数", $"{item.Count} 次");
        var progressTile = CreateDialogInfoTile("\uE9D2", "保底进度", $"{Math.Clamp(item.Count / Math.Max(1, item.PityThreshold), 0, 1):P0}");
        var latestTile = CreateDialogInfoTile("\uE787", "最近记录", item.LastCapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        Grid.SetColumn(progressTile, 1);
        Grid.SetRow(latestTile, 1);
        Grid.SetColumnSpan(latestTile, 2);
        detailGrid.Children.Add(countTile);
        detailGrid.Children.Add(progressTile);
        detailGrid.Children.Add(latestTile);
        content.Children.Add(detailGrid);
        content.Children.Add(new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xC4, 0x2B, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xC4, 0x2B, 0x1C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "删除奇遇统计不会删除已记录的异色精灵，但会影响后续保底计数判断。",
                Foreground = GetResourceBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x72, 0x76, 0x83))),
                TextWrapping = TextWrapping.Wrap
            }
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "删除奇遇条目",
            Content = content,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        var editButton = CreateDialogIconButton("\uE70F", "编辑", GetResourceBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x72, 0x76, 0x83))));
        var deleteButton = CreateDialogIconButton("\uE74D", "删除", new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)));
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

        var headerCard = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x63, 0x66, 0xF1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0x63, 0x66, 0xF1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var headerGrid = new Grid { ColumnSpacing = 10 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(new Border
        {
            Width = 42,
            Height = 42,
            VerticalAlignment = VerticalAlignment.Center,
            Background = GetResourceBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF))),
            CornerRadius = new CornerRadius(8),
            Child = CreateDialogAvatarContent(
                item.Avatar,
                "\uE77B",
                GetResourceBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x63, 0x66, 0xF1))),
                19)
        });
        var titlePanel = new StackPanel { Spacing = 3 };
        titlePanel.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = $"{FormatSeasonDisplay(seasonId)} · 奇遇统计",
            FontSize = 13,
            Foreground = GetResourceBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x5F, 0x64, 0x73))),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        Grid.SetColumn(titlePanel, 1);
        Grid.SetColumn(editButton, 2);
        Grid.SetColumn(deleteButton, 3);
        headerGrid.Children.Add(titlePanel);
        headerGrid.Children.Add(editButton);
        headerGrid.Children.Add(deleteButton);
        headerCard.Child = headerGrid;

        var detailGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10
        };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var countTile = CreateDialogInfoTile("\uE81D", "奇遇计数", $"{item.Count} 次");
        var progressTile = CreateDialogInfoTile("\uE9D2", "保底进度", $"{Math.Clamp(item.Count / Math.Max(1, item.PityThreshold), 0, 1):P0}");
        var latestTile = CreateDialogInfoTile("\uE787", "最近记录", item.LastCapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        Grid.SetColumn(progressTile, 1);
        Grid.SetRow(latestTile, 1);
        Grid.SetColumnSpan(latestTile, 2);
        detailGrid.Children.Add(countTile);
        detailGrid.Children.Add(progressTile);
        detailGrid.Children.Add(latestTile);

        var content = new StackPanel
        {
            Width = 420,
            Spacing = 14,
            Children =
            {
                headerCard,
                detailGrid
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

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "异色详情",
            Content = flipView,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };

        _activeShinyDetailDialog = dialog;
        _requestedShinyDetailAction = StatisticDetailAction.None;
        _requestedShinyDetailItem = null;

        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_activeShinyDetailDialog, dialog))
            {
                _activeShinyDetailDialog = null;
            }
        }

        var action = _requestedShinyDetailAction;
        var selectedDetail = _requestedShinyDetailItem;
        _requestedShinyDetailAction = StatisticDetailAction.None;
        _requestedShinyDetailItem = null;
        if (selectedDetail is null)
        {
            return;
        }

        if (action == StatisticDetailAction.Edit)
        {
            await EditShinyCaptureAsync(selectedDetail);
        }
        else if (action == StatisticDetailAction.Delete)
        {
            await DeleteShinyCaptureAsync(selectedDetail);
        }
    }

    private void ShinyCaptureEditButton_Click(object sender, RoutedEventArgs e)
    {
        RequestShinyDetailAction(sender, StatisticDetailAction.Edit);
    }

    private void ShinyCaptureDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        RequestShinyDetailAction(sender, StatisticDetailAction.Delete);
    }

    private void RequestShinyDetailAction(object sender, StatisticDetailAction action)
    {
        if (sender is not FrameworkElement { DataContext: ShinyCaptureDetailItem item })
        {
            return;
        }

        _requestedShinyDetailAction = action;
        _requestedShinyDetailItem = item;
        _activeShinyDetailDialog?.Hide();
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
        if (await ConfirmDeleteShinyCaptureAsync(item))
        {
            await ViewModel.DeleteShinyCaptureAsync(item);
        }
    }

    private async Task<bool> ConfirmDeleteShinyCaptureAsync(ShinyCaptureDetailItem item)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "删除异色记录",
            Content = CreateShinyDeleteDialogContent(item),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static StackPanel CreateShinyDeleteDialogContent(ShinyCaptureDetailItem item)
    {
        var content = new StackPanel
        {
            Width = 420,
            Spacing = 14
        };

        content.Children.Add(CreateDialogHeaderCard(
            "\uE74D",
            item.Name,
            $"{item.SeasonDisplay} · {item.PositionDisplay}",
            "该操作只删除当前这一只异色记录。",
            new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
            new SolidColorBrush(Color.FromArgb(0x1A, 0xC4, 0x2B, 0x1C)),
            item.Avatar));

        var detailGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10
        };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var encounterTile = CreateDialogInfoTile("\uE81D", "异色前奇遇", item.EncounterCountDisplay);
        var dateTile = CreateDialogInfoTile("\uE787", "获得日期", item.CapturedDateDisplay);
        var timeTile = CreateDialogInfoTile("\uE823", "获得时间", item.CapturedTimeDisplay);
        Grid.SetColumn(dateTile, 1);
        Grid.SetRow(timeTile, 1);
        Grid.SetColumnSpan(timeTile, 2);
        detailGrid.Children.Add(encounterTile);
        detailGrid.Children.Add(dateTile);
        detailGrid.Children.Add(timeTile);

        content.Children.Add(detailGrid);
        content.Children.Add(new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xC4, 0x2B, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xC4, 0x2B, 0x1C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "删除后不会清空或改动同名精灵的其他异色记录，也不会回滚奇遇计数。",
                Foreground = GetResourceBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x72, 0x76, 0x83))),
                TextWrapping = TextWrapping.Wrap
            }
        });
        return content;
    }

    private static Border CreateDialogHeaderCard(
        string glyph,
        string title,
        string subtitle,
        string description,
        Brush iconBrush,
        Brush backgroundBrush,
        BitmapImage? avatar = null)
    {
        var card = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            Background = backgroundBrush,
            BorderBrush = iconBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBox = new Border
        {
            Width = 42,
            Height = 42,
            VerticalAlignment = VerticalAlignment.Center,
            Background = GetResourceBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF))),
            CornerRadius = new CornerRadius(8),
            Child = CreateDialogAvatarContent(avatar, glyph, iconBrush, 19)
        };
        var textPanel = new StackPanel { Spacing = 3 };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = GetResourceBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x5F, 0x64, 0x73))),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = GetResourceBrush("TextFillColorTertiaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x72, 0x76, 0x83))),
            TextWrapping = TextWrapping.Wrap
        });

        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(iconBox);
        grid.Children.Add(textPanel);
        card.Child = grid;
        return card;
    }

    private static UIElement CreateDialogAvatarContent(
        BitmapImage? avatar,
        string fallbackGlyph,
        Brush fallbackBrush,
        double fallbackFontSize)
    {
        if (avatar is not null)
        {
            return new Image
            {
                Margin = new Thickness(2),
                Source = avatar,
                Stretch = Stretch.Uniform
            };
        }

        return new FontIcon
        {
            Glyph = fallbackGlyph,
            FontSize = fallbackFontSize,
            Foreground = fallbackBrush,
            FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
        };
    }

    private static Border CreateDialogInfoTile(string glyph, string label, string value)
    {
        var tile = new Border
        {
            Padding = new Thickness(12),
            Background = GetResourceBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF))),
            BorderBrush = GetResourceBrush("CardStrokeColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            Foreground = GetResourceBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x63, 0x66, 0xF1))),
            FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
        });
        var textPanel = new StackPanel { Spacing = 3 };
        textPanel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = GetResourceBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xFF, 0x72, 0x76, 0x83))),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);
        tile.Child = grid;
        return tile;
    }

    private static Brush GetResourceBrush(string key, Brush fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : fallback;
    }

    private static Button CreateDialogIconButton(string glyph, string tooltip, Brush foreground)
    {
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = GetResourceBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF))),
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 14,
                Foreground = foreground,
                FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
            }
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static string FormatSeasonDisplay(string seasonId)
    {
        return string.IsNullOrWhiteSpace(seasonId) || seasonId.EndsWith("赛季", StringComparison.Ordinal)
            ? seasonId
            : $"{seasonId}赛季";
    }

    private async void ConfirmPendingShinyButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConfirmLatestPendingShinyAsync();
    }

    private async void DiscardPendingShinyButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DiscardLatestPendingShinyAsync();
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
