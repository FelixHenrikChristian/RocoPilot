using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        var uidTextBox = new TextBox
        {
            Header = "UID",
            Text = suggestedUid,
            MaxLength = 64,
            PlaceholderText = "请输入 UID",
            SelectionStart = suggestedUid.Length
        };
        var validationText = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "确认统计账号",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = BuildDescription(request),
                        TextWrapping = TextWrapping.Wrap
                    },
                    uidTextBox,
                    validationText
                }
            },
            PrimaryButtonText = "确认并使用",
            SecondaryButtonText = "重新识别",
            CloseButtonText = "稍后处理",
            DefaultButton = ContentDialogButton.Primary
        };

        var confirmedUid = string.Empty;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (StatisticsUidRules.TryNormalize(uidTextBox.Text, out confirmedUid))
            {
                validationText.Visibility = Visibility.Collapsed;
                return;
            }

            args.Cancel = true;
            validationText.Text = "请输入至少一位数字。符号和空格会自动忽略。";
            validationText.Visibility = Visibility.Visible;
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
                ? $"OCR 未能识别出 UID。{request.Message} 你可以手动输入，或重新识别。"
                : $"OCR 未能识别出 UID。已填入当前统计账号 {request.SuggestedUid}，请确认本次是否使用该账号，也可以修改或重新识别。";
        }

        return $"OCR 识别到 UID {request.SuggestedUid}。请确认这是本次记录使用的账号；如有错误，可以直接修改后确认。";
    }
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
