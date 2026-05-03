using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.TextRecognition;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace RocoPilot.Views.Test;

public sealed partial class TextRecognitionTestPage : Page
{
    private readonly ITextRecognitionService _textRecognitionService;
    private IReadOnlyList<TextRecognitionMethodOption> _recognitionMethods = Array.Empty<TextRecognitionMethodOption>();
    private byte[]? _loadedImageBytes;
    private string? _loadedSourceName;
    private bool _waitingForScreenClip;
    private bool _isRecognizing;

    public TextRecognitionTestPage()
    {
        _textRecognitionService = App.GetService<ITextRecognitionService>();

        InitializeComponent();

        LoadRecognitionMethods();
        Clipboard.ContentChanged += Clipboard_ContentChanged;
        Unloaded += TextRecognitionTestPage_Unloaded;
    }

    private void LoadRecognitionMethods()
    {
        _recognitionMethods = _textRecognitionService.GetMethods();
        RecognitionMethodComboBox.ItemsSource = _recognitionMethods;
        RecognitionMethodComboBox.SelectedItem = _recognitionMethods.FirstOrDefault(method => method.IsAvailable)
            ?? _recognitionMethods.FirstOrDefault();

        UpdateRecognitionMethodStatus();
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

    private async void RecognizeButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loadedImageBytes is null)
        {
            ResultStatusText.Text = "请先导入图像或从剪贴板读取截图";
            return;
        }

        if (RecognitionMethodComboBox.SelectedItem is not TextRecognitionMethodOption selectedMethod)
        {
            ResultStatusText.Text = "请选择识别方法";
            return;
        }

        if (!selectedMethod.IsAvailable)
        {
            ResultStatusText.Text = selectedMethod.UnavailableReason ?? "当前识别方法不可用";
            return;
        }

        SetRecognitionBusy(true);
        ResultTextBox.Text = string.Empty;
        ResultStatusText.Text = $"正在识别：{_loadedSourceName ?? "当前图像"}";

        try
        {
            var result = await _textRecognitionService.RecognizeAsync(_loadedImageBytes, selectedMethod.Method);
            ResultTextBox.Text = result.Text;

            ResultStatusText.Text = result.Text.Length == 0
                ? BuildFinishedStatus("未识别到文字", result)
                : BuildFinishedStatus($"识别完成：{result.Lines.Count} 行，{result.WordCount} 个词", result);
        }
        catch (Exception ex)
        {
            ResultStatusText.Text = $"识别失败：{ex.Message}";
        }
        finally
        {
            SetRecognitionBusy(false);
        }
    }

    private void RecognitionMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRecognitionMethodStatus();
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
        _loadedImageBytes = await ReadStreamBytesAsync(stream);
        _loadedSourceName = sourceName;

        using var previewStream = await CreateReadStreamAsync(_loadedImageBytes);
        var imageSource = new BitmapImage();
        await imageSource.SetSourceAsync(previewStream);

        PreviewImage.Source = imageSource;
        PreviewEmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        InputStatusText.Text = $"已加载：{sourceName}";
        ResultStatusText.Text = "图像已准备，点击开始识别";
        ResultTextBox.Text = string.Empty;

        UpdateRecognitionMethodStatus();
    }

    private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
    {
        if (stream.Size == 0)
        {
            throw new InvalidOperationException("图像内容为空。");
        }

        if (stream.Size > int.MaxValue)
        {
            throw new InvalidOperationException("图像文件过大，无法读取。");
        }

        var inputStream = stream.GetInputStreamAt(0);
        using var reader = new DataReader(inputStream);
        var byteCount = checked((uint)stream.Size);
        var bytesLoaded = await reader.LoadAsync(byteCount);
        if (bytesLoaded != byteCount)
        {
            throw new InvalidOperationException("图像内容未完整读取。");
        }

        var bytes = new byte[checked((int)bytesLoaded)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<InMemoryRandomAccessStream> CreateReadStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);

        try
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void UpdateRecognitionMethodStatus()
    {
        if (RecognitionMethodComboBox.SelectedItem is not TextRecognitionMethodOption selectedMethod)
        {
            ResultStatusText.Text = "没有可用的识别方法";
            SetRecognitionBusy(_isRecognizing);
            return;
        }

        if (!selectedMethod.IsAvailable)
        {
            ResultStatusText.Text = selectedMethod.UnavailableReason ?? "当前识别方法不可用";
        }
        else if (_loadedImageBytes is null)
        {
            ResultStatusText.Text = selectedMethod.Description;
        }
        else
        {
            ResultStatusText.Text = "图像已准备，点击开始识别";
        }

        SetRecognitionBusy(_isRecognizing);
    }

    private static string BuildFinishedStatus(string prefix, TextRecognitionResult result)
    {
        return string.IsNullOrWhiteSpace(result.LanguageName)
            ? $"{prefix} · {result.MethodName}"
            : $"{prefix} · {result.MethodName} · 识别语言：{result.LanguageName}";
    }

    private void SetRecognitionBusy(bool isRecognizing)
    {
        _isRecognizing = isRecognizing;
        ImportImageButton.IsEnabled = !isRecognizing;
        OpenScreenClipButton.IsEnabled = !isRecognizing;
        ReadClipboardButton.IsEnabled = !isRecognizing;
        RecognizeButton.IsEnabled = _loadedImageBytes is not null
            && RecognitionMethodComboBox.SelectedItem is TextRecognitionMethodOption selectedMethod
            && selectedMethod.IsAvailable
            && !isRecognizing;
    }

    private void ShowInputError(string message, Exception ex)
    {
        InputStatusText.Text = $"{message}：{ex.Message}";
    }
}
