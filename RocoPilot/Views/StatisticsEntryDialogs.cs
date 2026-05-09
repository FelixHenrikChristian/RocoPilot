using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
}

internal sealed record StatisticEntryEditResult(string Name, int Count);

internal sealed record ShinyEntryAddResult(
    string Name,
    int Count,
    DateTimeOffset CapturedAt,
    bool ResetEncounterCount,
    int? EncounterCountBeforeCapture);
