using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Controls;
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
        formGrid.Children.Add(SpiritNamePreview.CreateField(nameTextBox, shiny: false));
        formGrid.Children.Add(countNumberBox);

        var content = new StackPanel
        {
            Width = Math.Min(400, Math.Max(240, xamlRoot.Size.Width - 96)),
            Spacing = 14,
            Children =
            {
                CreateDialogSection(
                    "\uE81D",
                    "条目信息",
                    "",
                    formGrid)
            }
        };

        var dialog = new AppContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = CreateScrollableForm(content, xamlRoot),
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
        var resetEncounterCheckBox = new CheckBox
        {
            Content = "清空该精灵奇遇计数",
            IsChecked = true,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        resetEncounterCheckBox.Loaded += (_, _) =>
        {
            var rectangle = FindTemplateChild<Microsoft.UI.Xaml.Shapes.Rectangle>(resetEncounterCheckBox, "NormalRectangle");
            if (rectangle is not null)
            {
                rectangle.RadiusX = 4;
                rectangle.RadiusY = 4;
            }
        };
        const string resetEncounterHelp = "需要清空：软件遗漏识别、手动补录等\n"
            + "无需清空：通过异色蛋等途径获取的异色，不占用奇遇保底";
        var helpButton = new Button
        {
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            BorderBrush = GetResourceBrush("TextFillColorSecondaryBrush", CreateBrush(0xFF, 0x72, 0x76, 0x83)),
            Background = CreateBrush(0x00, 0x00, 0x00, 0x00),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = "?", FontSize = 13 }
        };
        AutomationProperties.SetName(helpButton, "清空奇遇计数说明");
        AutomationProperties.SetHelpText(helpButton, resetEncounterHelp);
        var helpToolTip = new ToolTip
        {
            Content = new TextBlock
            {
                Text = resetEncounterHelp,
                MaxWidth = 400,
                TextWrapping = TextWrapping.Wrap
            }
        };
        ToolTipService.SetToolTip(helpButton, helpToolTip);
        var resetEncounterPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { resetEncounterCheckBox, helpButton }
        };

        var capturedDatePicker = new CalendarDatePicker
        {
            Header = "获取日期",
            MinWidth = 0,
            Date = now,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var capturedTimePicker = new TimePicker
        {
            Header = "获取时间",
            Time = now.TimeOfDay,
            MinuteIncrement = 1,
            MinWidth = 0,
            ClockIdentifier = "24HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var encounterCountNumberBox = new NumberBox
        {
            Header = "异色前奇遇次数",
            Minimum = 0,
            Value = double.NaN,
            IsEnabled = false,
            PlaceholderText = "自动使用当前奇遇计数",
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var manualEncounterCount = 0d;
        resetEncounterCheckBox.Checked += (_, _) =>
        {
            manualEncounterCount = double.IsNaN(encounterCountNumberBox.Value) ? 0 : encounterCountNumberBox.Value;
            encounterCountNumberBox.Value = double.NaN;
            encounterCountNumberBox.IsEnabled = false;
        };
        resetEncounterCheckBox.Unchecked += (_, _) =>
        {
            encounterCountNumberBox.IsEnabled = true;
            encounterCountNumberBox.Value = manualEncounterCount;
        };

        var formGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 4; row++)
        {
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var nameWithAvatar = SpiritNamePreview.CreateField(nameTextBox, shiny: true);
        Grid.SetColumnSpan(nameWithAvatar, 2);
        Grid.SetRow(countNumberBox, 1);
        Grid.SetRow(encounterCountNumberBox, 1);
        Grid.SetColumn(encounterCountNumberBox, 1);
        var capturedDateField = CreateDateTimeField("获取日期", capturedDatePicker);
        var capturedTimeField = CreateDateTimeField("获取时间", capturedTimePicker);
        Grid.SetRow(capturedDateField, 2);
        Grid.SetRow(capturedTimeField, 2);
        Grid.SetColumn(capturedTimeField, 1);
        Grid.SetRow(resetEncounterPanel, 3);
        Grid.SetColumnSpan(resetEncounterPanel, 2);
        formGrid.Children.Add(nameWithAvatar);
        formGrid.Children.Add(countNumberBox);
        formGrid.Children.Add(encounterCountNumberBox);
        formGrid.Children.Add(capturedDateField);
        formGrid.Children.Add(capturedTimeField);
        formGrid.Children.Add(resetEncounterPanel);
        ToolTipService.SetToolTip(encounterCountNumberBox,
            "勾选清空时自动记录清空前的奇遇次数；未勾选时可自行填写。");
        encounterCountNumberBox.PlaceholderText = "使用当前计数";

        var content = new Border
        {
            Width = Math.Min(440, Math.Max(240, xamlRoot.Size.Width - 96)),
            Style = (Style)Application.Current.Resources["DialogCardStyle"],
            Child = formGrid
        };
        var scrollViewer = CreateScrollableForm(content, xamlRoot);

        var dialog = new AppContentDialog
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
        var resetEncounterCount = resetEncounterCheckBox.IsChecked == true;
        var capturedAt = ResolveCapturedAt(capturedDatePicker, capturedTimePicker, now);
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
            Header = "异色前奇遇次数",
            Minimum = 0,
            Value = item.EncounterCountBeforeCapture,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var capturedDatePicker = new CalendarDatePicker
        {
            Header = "获取日期",
            MinWidth = 0,
            Date = capturedAt,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var capturedTimePicker = new TimePicker
        {
            Header = "获取时间",
            Time = capturedAt.TimeOfDay,
            MinuteIncrement = 1,
            MinWidth = 0,
            ClockIdentifier = "24HourClock",
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

        var nameWithAvatar = SpiritNamePreview.CreateField(nameTextBox, shiny: true);
        Grid.SetColumnSpan(nameWithAvatar, 2);
        Grid.SetRow(encounterCountNumberBox, 1);
        Grid.SetColumnSpan(encounterCountNumberBox, 2);
        var capturedDateField = CreateDateTimeField("获取日期", capturedDatePicker);
        var capturedTimeField = CreateDateTimeField("获取时间", capturedTimePicker);
        Grid.SetRow(capturedDateField, 2);
        Grid.SetRow(capturedTimeField, 2);
        Grid.SetColumn(capturedTimeField, 1);
        formGrid.Children.Add(nameWithAvatar);
        formGrid.Children.Add(encounterCountNumberBox);
        formGrid.Children.Add(capturedDateField);
        formGrid.Children.Add(capturedTimeField);

        var content = new StackPanel
        {
            Width = Math.Min(440, Math.Max(240, xamlRoot.Size.Width - 96)),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = $"{item.SeasonDisplay} · {item.PositionDisplay}",
                    Style = (Style)Application.Current.Resources["DialogDescriptionTextStyle"]
                },
                new Border
                {
                    Style = (Style)Application.Current.Resources["DialogCardStyle"],
                    Child = formGrid
                }
            }
        };

        var dialog = new AppContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "编辑异色记录",
            Content = CreateScrollableForm(content, xamlRoot),
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
            ResolveCapturedAt(capturedDatePicker, capturedTimePicker, capturedAt));
    }

    private static StackPanel CreateDateTimeField(string label, Control picker)
    {
        const double fieldHeight = 34;
        picker.Height = fieldHeight;
        picker.MinWidth = 0;
        picker.CornerRadius = new CornerRadius(8);
        picker.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(picker, label);
        if (picker is CalendarDatePicker datePicker)
        {
            datePicker.Header = null;
        }
        else if (picker is TimePicker timePicker)
        {
            timePicker.Header = null;
            // The native TimePicker template has a minimum width on its inner
            // button, independent of the control's own MinWidth.
            timePicker.Resources["TimePickerThemeMinWidth"] = 0d;
            timePicker.Loaded += (_, _) =>
            {
                var button = FindTemplateChild<Button>(timePicker, "FlyoutButton");
                if (button is not null)
                {
                    button.MinWidth = 0;
                    button.Height = fieldHeight;
                    button.CornerRadius = timePicker.CornerRadius;
                    button.ApplyTemplate();
                    // Apply the radius to the element that paints the background,
                    // including templates that do not forward the picker's radius.
                    var presenter = FindTemplateChild<ContentPresenter>(button, "ContentPresenter");
                    if (presenter is not null)
                    {
                        presenter.CornerRadius = timePicker.CornerRadius;
                    }
                }
            };
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label },
                picker
            }
        };
    }

    private static T? FindTemplateChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T element && element.Name == name) return element;
            if (FindTemplateChild<T>(child, name) is { } match) return match;
        }
        return null;
    }

    private static ScrollViewer CreateScrollableForm(UIElement content, XamlRoot xamlRoot)
    {
        return new ScrollViewer
        {
            MaxHeight = Math.Min(560, Math.Max(120, xamlRoot.Size.Height - 200)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = content
        };
    }

    private static Border CreateDialogSection(string glyph, string title, string subtitle, UIElement body)
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["DialogCardStyle"]
        };
        var panel = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
        });
        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = GetResourceBrush("TextFillColorSecondaryBrush", CreateBrush(0xFF, 0x72, 0x76, 0x83)),
                TextWrapping = TextWrapping.Wrap
            });
        }
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

    private static DateTimeOffset ResolveCapturedAt(
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
