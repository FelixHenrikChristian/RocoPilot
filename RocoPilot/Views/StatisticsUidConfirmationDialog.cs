using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Models.Statistics;

namespace RocoPilot.Views;

public static class StatisticsUidConfirmationDialog
{
    public static async Task<string?> ShowAsync(XamlRoot? xamlRoot, string detectedUid)
    {
        if (xamlRoot is null)
        {
            return null;
        }

        var uidTextBox = new TextBox
        {
            Header = "UID",
            Text = detectedUid,
            PlaceholderText = "请输入纯数字 UID",
            SelectionStart = detectedUid.Length
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
                        Text = $"OCR 识别到 UID {detectedUid}，但统计数据中没有这个账号。请检查识别结果；如有错误，可以直接修改后再确认。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    uidTextBox,
                    validationText
                }
            },
            PrimaryButtonText = "确认并使用",
            CloseButtonText = "暂不使用",
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
            validationText.Text = "UID 只能包含数字，请检查后重试。";
            validationText.Visibility = Visibility.Visible;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? confirmedUid
            : null;
    }
}
