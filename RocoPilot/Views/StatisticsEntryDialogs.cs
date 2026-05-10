using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RocoPilot.ViewModels;

using Windows.UI;

namespace RocoPilot.Views;

internal static class StatisticsEntryDialogs
{
    public static async Task<StatisticEntryEditResult?> ShowStatisticEntryAsync(
        XamlRoot? xamlRoot,
        string title,
        string primaryButtonText,
        string name = "",
        int count = 1)
    {
        if (xamlRoot is null)
        {
            return null;
        }

        var nameTextBox = new TextBox
        {
            Header = "精灵名",
            MaxLength = 32,
            PlaceholderText = "输入精灵名",
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
        var formGrid = new Grid
        {
            RowSpacing = 12
        };
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(countNumberBox, 1);
        formGrid.Children.Add(nameTextBox);
        formGrid.Children.Add(countNumberBox);

        var isEdit = !string.IsNullOrWhiteSpace(name);
        var content = new StackPanel
        {
            Width = 400,
            Spacing = 14,
            Children =
            {
                CreateDialogHeaderCard(
                    isEdit ? "\uE70F" : "\uE710",
                    isEdit ? name : "新增奇遇",
                    "赛季奇遇统计",
                    isEdit ? $"当前计数：{Math.Max(1, count)} 次" : "手动补充未被自动统计的奇遇记录。",
                    CreateBrush(0xFF, 0x63, 0x66, 0xF1),
                    CreateBrush(0x1F, 0x63, 0x66, 0xF1)),
                CreateDialogSection(
                    "\uE81D",
                    "条目信息",
                    "精灵名用于和当前赛季的奇遇统计记录匹配。",
                    formGrid)
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

    public static async Task<ShinyEntryAddResult?> ShowShinyEntryAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var nameTextBox = new TextBox
        {
            Header = "精灵名",
            MaxLength = 32,
            PlaceholderText = "输入精灵名"
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
            MinWidth = 220,
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
        var basicGrid = new Grid
        {
            RowSpacing = 12
        };
        basicGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        basicGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(countNumberBox, 1);
        basicGrid.Children.Add(nameTextBox);
        basicGrid.Children.Add(countNumberBox);

        var modeHintTextBlock = new TextBlock
        {
            Text = "适用于软件漏识别但你刚刚抓到异色的情况。确认后会清空当前对应精灵的奇遇计数。",
            Foreground = GetResourceBrush("TextFillColorSecondaryBrush", CreateBrush(0xFF, 0x72, 0x76, 0x83)),
            TextWrapping = TextWrapping.Wrap
        };
        var modePanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                addModeComboBox,
                new Border
                {
                    Padding = new Thickness(12),
                    Background = CreateBrush(0x12, 0x63, 0x66, 0xF1),
                    BorderBrush = CreateBrush(0x24, 0x63, 0x66, 0xF1),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Child = modeHintTextBlock
                }
            }
        };

        var historicalGrid = new Grid
        {
            RowSpacing = 12
        };
        historicalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        historicalGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historicalGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historicalGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(capturedTimePicker, 1);
        Grid.SetRow(encounterCountNumberBox, 2);
        historicalGrid.Children.Add(capturedDatePicker);
        historicalGrid.Children.Add(capturedTimePicker);
        historicalGrid.Children.Add(encounterCountNumberBox);

        var historicalSection = CreateDialogSection(
            "\uE787",
            "历史补录信息",
            "仅在软件使用前就已获得异色时使用，请在下方补充获得时间和异色前奇遇。",
            historicalGrid);
        historicalSection.Visibility = Visibility.Collapsed;

        addModeComboBox.SelectionChanged += (_, _) =>
        {
            var isHistorical = addModeComboBox.SelectedIndex == 1;
            historicalSection.Visibility = isHistorical
                ? Visibility.Visible
                : Visibility.Collapsed;
            modeHintTextBlock.Text = isHistorical
                ? "适用于软件使用前就已经获得的异色。请在下方补充获得时间和异色前奇遇，保存后不会清空当前对应精灵的奇遇计数。"
                : "适用于软件漏识别但你刚刚抓到异色的情况。确认后会清空当前对应精灵的奇遇计数。";
        };

        var content = new StackPanel
        {
            Width = 440,
            Spacing = 14,
            Children =
            {
                CreateDialogHeaderCard(
                    "\uE734",
                    "新增异色",
                    "异色精灵统计",
                    "选择新增类型后，软件会按对应规则处理奇遇计数。",
                    CreateBrush(0xFF, 0x63, 0x66, 0xF1),
                    CreateBrush(0x1F, 0x63, 0x66, 0xF1)),
                CreateDialogSection(
                    "\uE71C",
                    "基础信息",
                    "新增记录会计入异色统计列表。",
                    basicGrid),
                CreateDialogSection(
                    "\uE8FD",
                    "新增类型",
                    "漏识别会清空对应奇遇，历史补录会保留当前奇遇。",
                    modePanel),
                historicalSection
            }
        };
        var scrollViewer = new ScrollViewer
        {
            MaxHeight = 560,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = content
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "新增异色条目",
            Content = scrollViewer,
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

    public static async Task<ShinyCaptureEditResult?> ShowShinyCaptureEditAsync(
        XamlRoot? xamlRoot,
        ShinyCaptureDetailItem item)
    {
        if (xamlRoot is null)
        {
            return null;
        }

        var capturedAt = item.CapturedAt.ToLocalTime();
        var nameTextBox = new TextBox
        {
            Header = "精灵名",
            MaxLength = 32,
            PlaceholderText = "输入精灵名",
            Text = item.Name
        };
        var encounterCountNumberBox = new NumberBox
        {
            Header = "异色前奇遇",
            Minimum = 0,
            Value = item.EncounterCountBeforeCapture,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var capturedDatePicker = new CalendarDatePicker
        {
            Header = "获取日期",
            Date = capturedAt,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var capturedTimePicker = new TimePicker
        {
            Header = "获取时间",
            Time = capturedAt.TimeOfDay,
            MinuteIncrement = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var formGrid = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumnSpan(nameTextBox, 2);
        Grid.SetRow(encounterCountNumberBox, 1);
        Grid.SetColumnSpan(encounterCountNumberBox, 2);
        Grid.SetRow(capturedDatePicker, 2);
        Grid.SetRow(capturedTimePicker, 2);
        Grid.SetColumn(capturedTimePicker, 1);
        formGrid.Children.Add(nameTextBox);
        formGrid.Children.Add(encounterCountNumberBox);
        formGrid.Children.Add(capturedDatePicker);
        formGrid.Children.Add(capturedTimePicker);

        var content = new StackPanel
        {
            Width = 440,
            Spacing = 14,
            Children =
            {
                CreateDialogHeaderCard(
                    "\uE70F",
                    item.Name,
                    $"{item.SeasonDisplay} · {item.PositionDisplay}",
                    $"当前记录：{item.EncounterCountDisplay}，{item.CapturedDateDisplay} {item.CapturedTimeDisplay}",
                    CreateBrush(0xFF, 0x63, 0x66, 0xF1),
                    CreateBrush(0x1F, 0x63, 0x66, 0xF1)),
                CreateDialogSection(
                    "\uE71C",
                    "记录信息",
                    "修改后只影响当前这一只异色记录。",
                    formGrid)
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "编辑异色记录",
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var encounterCount = double.IsNaN(encounterCountNumberBox.Value)
            ? 0
            : Math.Max(0, (int)Math.Round(encounterCountNumberBox.Value));
        return new ShinyCaptureEditResult(
            nameTextBox.Text,
            encounterCount,
            ResolveHistoricalCapturedAt(capturedDatePicker, capturedTimePicker, capturedAt));
    }

    private static Border CreateDialogHeaderCard(
        string glyph,
        string title,
        string subtitle,
        string description,
        Brush iconBrush,
        Brush backgroundBrush)
    {
        var card = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            Background = backgroundBrush,
            BorderBrush = CreateBrush(0x24, 0x63, 0x66, 0xF1),
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
            Background = CreateBrush(0x24, 0xFF, 0xFF, 0xFF),
            CornerRadius = new CornerRadius(8),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 19,
                Foreground = iconBrush,
                FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
            }
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
            Foreground = GetResourceBrush("TextFillColorSecondaryBrush", CreateBrush(0xFF, 0x5F, 0x64, 0x73)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = GetResourceBrush("TextFillColorTertiaryBrush", CreateBrush(0xFF, 0x72, 0x76, 0x83)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });

        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(iconBox);
        grid.Children.Add(textPanel);
        card.Child = grid;
        return card;
    }

    private static Border CreateDialogSection(string glyph, string title, string subtitle, UIElement body)
    {
        var card = new Border
        {
            Padding = new Thickness(16),
            Background = GetResourceBrush("ControlFillColorDefaultBrush", CreateBrush(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = GetResourceBrush("CardStrokeColorDefaultBrush", CreateBrush(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var panel = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = CreateBrush(0xFF, 0x63, 0x66, 0xF1),
            FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
        });
        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = GetResourceBrush("TextFillColorSecondaryBrush", CreateBrush(0xFF, 0x72, 0x76, 0x83)),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(textPanel, 1);
        header.Children.Add(textPanel);
        panel.Children.Add(header);
        panel.Children.Add(body);
        card.Child = panel;
        return card;
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
    }

    private static Brush GetResourceBrush(string key, Brush fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : fallback;
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
}

internal sealed record StatisticEntryEditResult(string Name, int Count);

internal sealed record ShinyEntryAddResult(
    string Name,
    int Count,
    DateTimeOffset CapturedAt,
    bool ResetEncounterCount,
    int? EncounterCountBeforeCapture);

internal sealed record ShinyCaptureEditResult(
    string Name,
    int EncounterCountBeforeCapture,
    DateTimeOffset CapturedAt);
