using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Controls;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Views;

public static class StatisticsUidConfirmationDialog
{
    public static async Task<StatisticsUidConfirmationDialogResult> ShowAsync(
        XamlRoot? xamlRoot,
        StatisticsUidConfirmationRequest request)
    {
        if (xamlRoot is null)
        {
            return StatisticsUidConfirmationDialogResult.Cancelled();
        }

        var suggestedUid = request.SuggestedUid ?? string.Empty;
        var secondaryBrush = GetBrush("TextFillColorSecondaryBrush");
        var normalBorderBrush = GetBrush("ControlStrokeColorDefaultBrush");
        var errorBrush = GetBrush("SystemFillColorCriticalBrush");
        const string inputHint = "本次自动统计将使用此账号；符号和空格会自动忽略。";
        var validationText = new TextBlock
        {
            Text = inputHint,
            FontSize = 11,
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap
        };
        var uidTextBox = new TextBox
        {
            Header = "统计账号 UID",
            Text = suggestedUid,
            Description = validationText,
            FontSize = 16,
            FontFamily = new FontFamily("Consolas"),
            MaxLength = 64,
            PlaceholderText = "请输入 UID",
            SelectionStart = suggestedUid.Length
        };
        var statusBrush = GetBrush(request.RecognitionSucceeded
            ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        var status = new Grid { ColumnSpacing = 12 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.Children.Add(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = GetBrush("ControlFillColorDefaultBrush"),
            Child = new FontIcon
            {
                Glyph = request.RecognitionSucceeded ? "\uE73E" : "\uE783",
                FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily,
                FontSize = 18,
                Foreground = statusBrush
            }
        });
        var statusText = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = request.RecognitionSucceeded ? "已识别到 UID" : "未识别到 UID",
                    Style = (Style)Application.Current.Resources["DialogSectionTitleStyle"],
                    Foreground = statusBrush
                },
                new TextBlock
                {
                    Text = BuildDescription(request),
                    FontSize = 12,
                    Foreground = secondaryBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        Grid.SetColumn(statusText, 1);
        status.Children.Add(statusText);
        var dialog = new AppContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "确认统计账号",
            Content = new ScrollViewer
            {
                MaxHeight = Math.Min(440, Math.Max(120, xamlRoot.Size.Height - 200)),
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border
                {
                    Width = Math.Min(400, Math.Max(240, xamlRoot.Size.Width - 96)),
                    Style = (Style)Application.Current.Resources["DialogCardStyle"],
                    Padding = new Thickness(16),
                    Child = new StackPanel
                    {
                        Spacing = 16,
                        Children = { status, uidTextBox }
                    }
                }
            },
            PrimaryButtonText = "确认并使用",
            SecondaryButtonText = "重新识别",
            CloseButtonText = "稍后处理",
            DefaultButton = ContentDialogButton.Primary
        };

        var confirmedUid = string.Empty;
        var hasValidationError = false;
        void ClearValidationError()
        {
            hasValidationError = false;
            validationText.Text = inputHint;
            validationText.Foreground = secondaryBrush;
            uidTextBox.BorderBrush = normalBorderBrush;
        }
        uidTextBox.TextChanged += (_, _) =>
        {
            if (hasValidationError && StatisticsUidRules.TryNormalize(uidTextBox.Text, out _))
            {
                ClearValidationError();
            }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (StatisticsUidRules.TryNormalize(uidTextBox.Text, out confirmedUid))
            {
                ClearValidationError();
                return;
            }

            args.Cancel = true;
            hasValidationError = true;
            validationText.Text = "请输入 1–32 位数字，符号和空格会自动忽略。";
            validationText.Foreground = errorBrush;
            uidTextBox.BorderBrush = errorBrush;
            uidTextBox.Focus(FocusState.Programmatic);
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary =>
                StatisticsUidConfirmationDialogResult.Confirmed(confirmedUid),
            ContentDialogResult.Secondary =>
                StatisticsUidConfirmationDialogResult.Retry(),
            _ => StatisticsUidConfirmationDialogResult.Cancelled()
        };
    }

    private static string BuildDescription(StatisticsUidConfirmationRequest request)
    {
        if (!request.RecognitionSucceeded)
        {
            return string.IsNullOrWhiteSpace(request.SuggestedUid)
                ? $"{request.Message?.Trim()} 手动输入账号，或重新识别。".Trim()
                : "已填入当前统计账号，请核对后使用，也可修改或重新识别。";
        }

        return "请核对本次统计账号，如有错误可直接修改。";
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}

public enum StatisticsUidConfirmationDialogAction
{
    Confirm,
    Retry,
    Cancel
}

public sealed record StatisticsUidConfirmationDialogResult(
    StatisticsUidConfirmationDialogAction Action,
    string? Uid)
{
    public static StatisticsUidConfirmationDialogResult Confirmed(string uid) =>
        new(StatisticsUidConfirmationDialogAction.Confirm, uid);

    public static StatisticsUidConfirmationDialogResult Retry() =>
        new(StatisticsUidConfirmationDialogAction.Retry, null);

    public static StatisticsUidConfirmationDialogResult Cancelled() =>
        new(StatisticsUidConfirmationDialogAction.Cancel, null);
}
