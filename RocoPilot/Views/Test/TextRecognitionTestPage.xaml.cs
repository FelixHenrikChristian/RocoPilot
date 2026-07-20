using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Helpers;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace RocoPilot.Views.Test;

public sealed partial class TextRecognitionTestPage : Page
{
    private const int ScreenClipClipboardTimeoutSeconds = 30;

    private readonly ITextRecognitionService _textRecognitionService;
    private readonly OnnxOcrV5SingleLineTextRecognitionTestBackend _onnxOcrV5SingleLineTestBackend;
    private IReadOnlyList<TextRecognitionMethodOption> _recognitionMethods = Array.Empty<TextRecognitionMethodOption>();
    private byte[]? _loadedImageBytes;
    private string? _loadedSourceName;
    private CancellationTokenSource? _screenClipClipboardCts;
    private bool _waitingForScreenClip;
    private bool _isRecognizing;

    public TextRecognitionTestPage()
    {
        _textRecognitionService = App.GetService<ITextRecognitionService>();
        _onnxOcrV5SingleLineTestBackend = App.GetService<OnnxOcrV5SingleLineTextRecognitionTestBackend>();

        InitializeComponent();

        LoadRecognitionMethods();
        Clipboard.ContentChanged += Clipboard_ContentChanged;
        Unloaded += TextRecognitionTestPage_Unloaded;
    }

    private void LoadRecognitionMethods()
    {
        _recognitionMethods = BuildRecognitionMethods(
            _textRecognitionService.GetMethods(),
            _onnxOcrV5SingleLineTestBackend.GetOption());
        RecognitionMethodComboBox.ItemsSource = _recognitionMethods;
        RecognitionMethodComboBox.SelectedItem = _recognitionMethods.FirstOrDefault(method => method.IsAvailable)
            ?? _recognitionMethods.FirstOrDefault();

        UpdateRecognitionMethodStatus();
    }

    private static IReadOnlyList<TextRecognitionMethodOption> BuildRecognitionMethods(
        IReadOnlyList<TextRecognitionMethodOption> methods,
        TextRecognitionMethodOption onnxSingleLineOption)
    {
        return
        [
            onnxSingleLineOption,
            .. methods.Where(method => method.Method != TextRecognitionMethod.OnnxOcrV5)
        ];
    }

    private async void ImportImageButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        string? filePath;
        try
        {
            filePath = FileDialogHelper.PickOpenFile(
                "导入图像",
                "图像文件 (*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.webp)|*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.webp|所有文件 (*.*)|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        }
        catch (Exception ex)
        {
            ShowInputError("无法打开文件选择窗口", ex);
            return;
        }

        if (filePath is null)
        {
            return;
        }

        try
        {
            await LoadPreviewFromFileAsync(filePath);
        }
        catch (Exception ex)
        {
            ShowInputError("无法读取图像文件", ex);
        }
    }

    private void OpenScreenClipButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var startClipboardSequence = GetClipboardSequenceNumber();
        _waitingForScreenClip = true;
        InputStatusText.Text = "截图工具已打开，截图完成后会尝试自动读取剪贴板";
        StartScreenClipClipboardPolling(startClipboardSequence);

        try
        {
            var launched = ShellLaunchHelper.LaunchUri(new Uri("ms-screenclip:"));
            if (launched)
            {
                return;
            }

            StopScreenClipClipboardPolling();
            InputStatusText.Text = "无法打开 Windows 截图工具";
        }
        catch (Exception ex)
        {
            StopScreenClipClipboardPolling();
            ShowInputError("无法打开 Windows 截图工具", ex);
        }
    }

    private async void ReadClipboardButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StopScreenClipClipboardPolling();
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
            var recognitionStartedAt = Stopwatch.GetTimestamp();
            var result = selectedMethod.Method == TextRecognitionMethod.OnnxOcrV5
                ? await _onnxOcrV5SingleLineTestBackend.RecognizeAsync(_loadedImageBytes)
                : await _textRecognitionService.RecognizeAsync(_loadedImageBytes, selectedMethod.Method);
            var recognitionElapsed = Stopwatch.GetElapsedTime(recognitionStartedAt);
            ResultTextBox.Text = result.Text;

            ResultStatusText.Text = result.Text.Length == 0
                ? BuildFinishedStatus("未识别到文字", result, recognitionElapsed)
                : BuildFinishedStatus($"识别完成：{result.Lines.Count} 行，{result.WordCount} 个词", result, recognitionElapsed);
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

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (await TryLoadPreviewFromClipboardAsync(showEmptyMessage: false))
                {
                    StopScreenClipClipboardPolling();
                }
            }
            catch (Exception ex)
            {
                StopScreenClipClipboardPolling();
                ShowInputError("无法读取截图图像", ex);
            }
        });
    }

    private void TextRecognitionTestPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Clipboard.ContentChanged -= Clipboard_ContentChanged;
        StopScreenClipClipboardPolling();
    }

    private async Task LoadPreviewFromFileAsync(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("图像文件不存在。", filePath);
        }

        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("图像内容为空。");
        }

        if (fileInfo.Length > int.MaxValue)
        {
            throw new InvalidOperationException("图像文件过大，无法读取。");
        }

        var bytes = await File.ReadAllBytesAsync(filePath);
        await LoadPreviewFromBytesAsync(bytes, Path.GetFileName(filePath));
    }

    private async Task<bool> TryLoadPreviewFromClipboardAsync(bool showEmptyMessage)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            if (showEmptyMessage)
            {
                InputStatusText.Text = "剪贴板中没有可读取的图像";
            }

            return false;
        }

        var bitmapReference = await content.GetBitmapAsync();
        using var stream = await bitmapReference.OpenReadAsync();
        await LoadPreviewFromStreamAsync(stream, "剪贴板图像");
        return true;
    }

    private async Task LoadPreviewFromStreamAsync(IRandomAccessStream stream, string sourceName)
    {
        var bytes = await ReadStreamBytesAsync(stream);
        await LoadPreviewFromBytesAsync(bytes, sourceName);
    }

    private async Task LoadPreviewFromBytesAsync(byte[] bytes, string sourceName)
    {
        _loadedImageBytes = bytes;
        _loadedSourceName = sourceName;

        using var previewStream = await CreateReadStreamAsync(bytes);
        var imageSource = new BitmapImage();
        await imageSource.SetSourceAsync(previewStream);

        PreviewImage.Source = imageSource;
        PreviewEmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        InputStatusText.Text = $"已加载：{sourceName}";
        ResultStatusText.Text = "图像已准备，点击开始识别";
        ResultTextBox.Text = string.Empty;

        UpdateRecognitionMethodStatus();
    }

    private void StartScreenClipClipboardPolling(uint startClipboardSequence)
    {
        StopScreenClipClipboardPolling();
        _waitingForScreenClip = true;

        _screenClipClipboardCts = new CancellationTokenSource();
        _ = PollClipboardForScreenClipAsync(startClipboardSequence, _screenClipClipboardCts.Token);
    }

    private void StopScreenClipClipboardPolling()
    {
        _waitingForScreenClip = false;
        _screenClipClipboardCts?.Cancel();
        _screenClipClipboardCts?.Dispose();
        _screenClipClipboardCts = null;
    }

    private async Task PollClipboardForScreenClipAsync(uint startClipboardSequence, CancellationToken cancellationToken)
    {
        var lastClipboardSequence = startClipboardSequence;
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(ScreenClipClipboardTimeoutSeconds);

        try
        {
            while (!cancellationToken.IsCancellationRequested
                && _waitingForScreenClip
                && DateTimeOffset.UtcNow < timeoutAt)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

                var currentClipboardSequence = GetClipboardSequenceNumber();
                if (currentClipboardSequence != 0 && currentClipboardSequence == lastClipboardSequence)
                {
                    continue;
                }

                lastClipboardSequence = currentClipboardSequence;
                if (await TryLoadPreviewFromClipboardAsync(showEmptyMessage: false))
                {
                    StopScreenClipClipboardPolling();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (COMException) when (!cancellationToken.IsCancellationRequested)
        {
            StopScreenClipClipboardPolling();
            InputStatusText.Text = "暂时无法读取剪贴板，请截图后点击读取剪贴板";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            StopScreenClipClipboardPolling();
            ShowInputError("无法读取截图图像", ex);
        }

        if (_waitingForScreenClip && !cancellationToken.IsCancellationRequested)
        {
            StopScreenClipClipboardPolling();
            InputStatusText.Text = "未检测到截图图像，请截图后点击读取剪贴板";
        }
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

    private static string BuildFinishedStatus(
        string prefix,
        TextRecognitionResult result,
        TimeSpan recognitionElapsed)
    {
        var status = string.IsNullOrWhiteSpace(result.LanguageName)
            ? $"{prefix} · {result.MethodName}"
            : $"{prefix} · {result.MethodName} · 识别语言：{result.LanguageName}";
        return $"{status} · 耗时 {recognitionElapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms";
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

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
