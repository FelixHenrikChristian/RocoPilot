using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace RocoPilot.Views.Test;

public sealed partial class TextRecognitionTestPage : Page
{
    private bool _waitingForScreenClip;

    public TextRecognitionTestPage()
    {
        InitializeComponent();

        Clipboard.ContentChanged += Clipboard_ContentChanged;
        Unloaded += TextRecognitionTestPage_Unloaded;
    }

    private async void ImportImageButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".tif");
        picker.FileTypeFilter.Add(".tiff");
        picker.FileTypeFilter.Add(".webp");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await LoadPreviewFromFileAsync(file);
        }
        catch (Exception ex)
        {
            ShowInputError("无法读取图像文件", ex);
        }
    }

    private async void OpenScreenClipButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _waitingForScreenClip = true;
        InputStatusText.Text = "截图工具已打开，截图完成后会尝试自动读取剪贴板";

        try
        {
            var launched = await Launcher.LaunchUriAsync(new Uri("ms-screenclip:"));
            if (launched)
            {
                return;
            }

            _waitingForScreenClip = false;
            InputStatusText.Text = "无法打开 Windows 截图工具";
        }
        catch (Exception ex)
        {
            _waitingForScreenClip = false;
            ShowInputError("无法打开 Windows 截图工具", ex);
        }
    }

    private async void ReadClipboardButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _waitingForScreenClip = false;
        try
        {
            await TryLoadPreviewFromClipboardAsync(showEmptyMessage: true);
        }
        catch (Exception ex)
        {
            ShowInputError("无法读取剪贴板图像", ex);
        }
    }

    private void RecognizeButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ResultTextBox.Text = "识别功能待接入。当前页面已准备好图像输入、截图读取和结果展示区域。";
    }

    private void Clipboard_ContentChanged(object? sender, object e)
    {
        if (!_waitingForScreenClip)
        {
            return;
        }

        _waitingForScreenClip = false;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await TryLoadPreviewFromClipboardAsync(showEmptyMessage: false);
            }
            catch (Exception ex)
            {
                ShowInputError("无法读取截图图像", ex);
            }
        });
    }

    private void TextRecognitionTestPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Clipboard.ContentChanged -= Clipboard_ContentChanged;
    }

    private async Task LoadPreviewFromFileAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        await LoadPreviewFromStreamAsync(stream, file.Name);
    }

    private async Task TryLoadPreviewFromClipboardAsync(bool showEmptyMessage)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            if (showEmptyMessage)
            {
                InputStatusText.Text = "剪贴板中没有可读取的图像";
            }

            return;
        }

        var bitmapReference = await content.GetBitmapAsync();
        using var stream = await bitmapReference.OpenReadAsync();
        await LoadPreviewFromStreamAsync(stream, "剪贴板图像");
    }

    private async Task LoadPreviewFromStreamAsync(IRandomAccessStream stream, string sourceName)
    {
        var imageSource = new BitmapImage();
        await imageSource.SetSourceAsync(stream);

        PreviewImage.Source = imageSource;
        PreviewEmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        RecognizeButton.IsEnabled = true;

        InputStatusText.Text = $"已加载：{sourceName}";
        ResultTextBox.Text = string.Empty;
    }

    private void ShowInputError(string message, Exception ex)
    {
        InputStatusText.Text = $"{message}：{ex.Message}";
    }
}
