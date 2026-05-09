using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Views.Windows;

using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace RocoPilot.Views.Test;

public sealed partial class RegionEditorPage : Page
{
    private static readonly string RegionScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Screenshots",
        "RecognitionRegions");

    private readonly IRecognitionRegionConfigService _configService;
    private readonly IGameWindowService _gameWindowService;
    private readonly IScreenCaptureService _captureService;
    private RecognitionRegionConfig? _currentConfig;

    public ObservableCollection<EditableRecognitionRegion> Regions
    {
        get;
    } = new();

    public ObservableCollection<CaptureMethodOption> CaptureMethods
    {
        get;
    } = new()
    {
        new(CaptureMethod.WindowsGraphicsCapture, "Windows Graphics Capture", "GPU/DirectX 窗口捕获"),
        new(CaptureMethod.PrintWindow, "PrintWindow", "兼容部分后台或被遮挡窗口"),
        new(CaptureMethod.BitBlt, "BitBlt", "快速捕获前台可见内容")
    };

    public RegionEditorPage()
    {
        _configService = App.GetService<IRecognitionRegionConfigService>();
        _gameWindowService = App.GetService<IGameWindowService>();
        _captureService = App.GetService<IScreenCaptureService>();

        InitializeComponent();

        CaptureMethodComboBox.ItemsSource = CaptureMethods;
        CaptureMethodComboBox.SelectedIndex = 0;
        RegionsListView.ItemsSource = Regions;

        Loaded += RegionEditorPage_Loaded;
    }

    private void RegionEditorPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_currentConfig is not null)
        {
            return;
        }

        var firstConfigPath = _configService.ListConfigPaths().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstConfigPath))
        {
            LoadConfig(firstConfigPath);
        }
    }

    private async void OpenRegionScreenshotFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RegionScreenshotDirectory);
            var folder = await StorageFolder.GetFolderFromPathAsync(RegionScreenshotDirectory);
            var opened = await Launcher.LaunchFolderAsync(folder);
            if (!opened)
            {
                ShowMessage($"无法打开截图文件夹：{RegionScreenshotDirectory}", InfoBarSeverity.Warning);
                return;
            }

            HideMessage();
        }
        catch (Exception ex)
        {
            ShowMessage($"打开截图文件夹失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void BrowseConfigButton_Click(object sender, RoutedEventArgs e)
    {
        string? filePath;
        try
        {
            filePath = FileDialogHelper.PickOpenFile(
                "选择识别区域配置文件",
                "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                Path.Combine(AppContext.BaseDirectory, "Configuration", "RecognitionRegions"));
        }
        catch (Exception ex)
        {
            ShowMessage($"打开文件选择窗口失败：{ex.Message}", InfoBarSeverity.Error);
            return;
        }

        if (filePath is null)
        {
            return;
        }

        try
        {
            LoadConfig(filePath);
        }
        catch (Exception ex)
        {
            ShowMessage($"载入配置文件失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConfig is null)
        {
            ShowMessage("请先选择一个配置文件", InfoBarSeverity.Warning);
            return;
        }

        if (!TryReadResolution(out var resolutionWidth, out var resolutionHeight))
        {
            return;
        }

        var regions = new List<RecognitionRegion>();
        var regionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Regions.Count; index++)
        {
            var editableRegion = Regions[index];
            if (!editableRegion.TryToModel(out var region, out var error))
            {
                ShowMessage($"第 {index + 1} 个区域无效：{error}", InfoBarSeverity.Warning);
                return;
            }

            if (!regionIds.Add(region.Id))
            {
                ShowMessage($"区域 ID 重复：{region.Id}", InfoBarSeverity.Warning);
                return;
            }

            regions.Add(region);
        }

        try
        {
            _currentConfig.ResolutionWidth = resolutionWidth;
            _currentConfig.ResolutionHeight = resolutionHeight;
            _currentConfig.Regions = regions;
            _configService.Save(_currentConfig);
            HideMessage();
        }
        catch (Exception ex)
        {
            ShowMessage($"保存失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void CaptureAddRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConfig is null)
        {
            ShowMessage("请先选择一个配置文件", InfoBarSeverity.Warning);
            return;
        }

        if (!TryGetCaptureRequest(out var targetWindow, out var selectedMethod))
        {
            return;
        }

        CaptureAddRegionButton.IsEnabled = false;

        CapturedFrame? frame = null;
        try
        {
            frame = await CaptureFrameAsync(targetWindow, selectedMethod.Method);
        }
        catch (Exception ex)
        {
            ShowMessage($"截图失败：{ex.Message}", InfoBarSeverity.Error);
            return;
        }
        finally
        {
            CaptureAddRegionButton.IsEnabled = true;
        }

        if (frame is null)
        {
            ShowMessage("未获取到游戏画面", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var selectionWindow = new RegionSelectionWindow(frame, targetWindow.DisplayName);
            var selectedRegion = await selectionWindow.SelectAsync();
            if (selectedRegion is null)
            {
                return;
            }

            selectedRegion = NormalizeRegionToClientArea(
                selectedRegion,
                frame,
                targetWindow,
                out var sourceWidth,
                out var sourceHeight);

            EnsureResolutionFromSource(sourceWidth, sourceHeight);
            if (!TryReadResolution(out var configWidth, out var configHeight))
            {
                return;
            }

            selectedRegion = ScaleRegion(selectedRegion, sourceWidth, sourceHeight, configWidth, configHeight);
            selectedRegion.Id = GenerateUniqueId("region");
            Regions.Add(EditableRecognitionRegion.FromModel(selectedRegion));
            HideMessage();
        }
        finally
        {
            frame.Dispose();
        }
    }

    private async void CaptureRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button captureButton
            || captureButton.DataContext is not EditableRecognitionRegion editableRegion)
        {
            return;
        }

        if (_currentConfig is null)
        {
            ShowMessage("请先选择一个配置文件", InfoBarSeverity.Warning);
            return;
        }

        if (!editableRegion.TryToModel(out var region, out var error))
        {
            ShowMessage($"区域无效：{error}", InfoBarSeverity.Warning);
            return;
        }

        if (!TryGetCaptureRequest(out var targetWindow, out var selectedMethod))
        {
            return;
        }

        captureButton.IsEnabled = false;

        CapturedFrame? frame = null;
        try
        {
            frame = await CaptureFrameAsync(targetWindow, selectedMethod.Method);
            if (frame is null)
            {
                ShowMessage("未获取到游戏画面", InfoBarSeverity.Warning);
                return;
            }

            if (!TryReadResolution(out var configWidth, out var configHeight))
            {
                return;
            }

            if (!TryCreateFrameRegion(
                region,
                frame,
                targetWindow,
                configWidth,
                configHeight,
                out var frameRegion,
                out var regionError))
            {
                ShowMessage(regionError, InfoBarSeverity.Warning);
                return;
            }

            var savedPath = await SaveRegionScreenshotAsync(frame, region, frameRegion);
            ShowMessage($"区域截图已保存：{savedPath}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage($"截图保存失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            frame?.Dispose();
            captureButton.IsEnabled = true;
        }
    }

    private void DeleteRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EditableRecognitionRegion region })
        {
            return;
        }

        Regions.Remove(region);
    }

    private bool TryGetCaptureRequest(
        out CaptureTargetWindow targetWindow,
        out CaptureMethodOption selectedMethod)
    {
        targetWindow = null!;
        selectedMethod = null!;

        if (CaptureMethodComboBox.SelectedItem is not CaptureMethodOption captureMethod)
        {
            ShowMessage("请先选择截图方式", InfoBarSeverity.Warning);
            return false;
        }

        var window = _gameWindowService.FindGameWindow();
        if (window is null)
        {
            ShowMessage($"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}", InfoBarSeverity.Warning);
            return false;
        }

        targetWindow = window;
        selectedMethod = captureMethod;
        return true;
    }

    private async Task<CapturedFrame?> CaptureFrameAsync(
        CaptureTargetWindow targetWindow,
        CaptureMethod captureMethod)
    {
        try
        {
            return await Task.Run(() => _captureService.Capture(targetWindow, captureMethod));
        }
        finally
        {
            _captureService.Release(targetWindow, captureMethod);
        }
    }

    private void LoadConfig(string path)
    {
        _currentConfig = _configService.LoadFromPath(path);
        SelectedConfigFileText.Text = Path.GetFileName(path);
        ResolutionWidthTextBox.Text = _currentConfig.ResolutionWidth > 0
            ? _currentConfig.ResolutionWidth.ToString()
            : string.Empty;
        ResolutionHeightTextBox.Text = _currentConfig.ResolutionHeight > 0
            ? _currentConfig.ResolutionHeight.ToString()
            : string.Empty;

        Regions.Clear();
        foreach (var region in _currentConfig.Regions)
        {
            Regions.Add(EditableRecognitionRegion.FromModel(region));
        }

        HideMessage();
    }

    private void EnsureResolutionFromSource(int width, int height)
    {
        if (!int.TryParse(ResolutionWidthTextBox.Text, out var currentWidth) || currentWidth <= 0)
        {
            ResolutionWidthTextBox.Text = width.ToString();
        }

        if (!int.TryParse(ResolutionHeightTextBox.Text, out var currentHeight) || currentHeight <= 0)
        {
            ResolutionHeightTextBox.Text = height.ToString();
        }
    }

    private bool TryReadResolution(out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!int.TryParse(ResolutionWidthTextBox.Text, out width) || width <= 0)
        {
            ShowMessage("配置宽度必须是大于 0 的整数", InfoBarSeverity.Warning);
            return false;
        }

        if (!int.TryParse(ResolutionHeightTextBox.Text, out height) || height <= 0)
        {
            ShowMessage("配置高度必须是大于 0 的整数", InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private string GenerateUniqueId(string prefix)
    {
        var existingIds = Regions
            .Select(region => region.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < 10000; index++)
        {
            var candidate = $"{prefix}-{index}";
            if (!existingIds.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{prefix}-{DateTimeOffset.Now:yyyyMMddHHmmss}";
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        EditorInfoBar.Message = message;
        EditorInfoBar.Severity = severity;
        EditorInfoBar.IsOpen = true;
    }

    private void HideMessage()
    {
        EditorInfoBar.IsOpen = false;
    }

    private static RecognitionRegion ScaleRegion(
        RecognitionRegion region,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
        {
            return region;
        }

        return new RecognitionRegion
        {
            Id = region.Id,
            X = ScaleValue(region.X, sourceWidth, targetWidth),
            Y = ScaleValue(region.Y, sourceHeight, targetHeight),
            Width = Math.Max(1, ScaleValue(region.Width, sourceWidth, targetWidth)),
            Height = Math.Max(1, ScaleValue(region.Height, sourceHeight, targetHeight)),
            Enabled = region.Enabled
        };
    }

    private static int ScaleValue(int value, int sourceSize, int targetSize)
    {
        if (sourceSize <= 0 || targetSize <= 0)
        {
            return value;
        }

        return (int)Math.Round(value * (double)targetSize / sourceSize);
    }

    private static bool TryCreateFrameRegion(
        RecognitionRegion region,
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        int configWidth,
        int configHeight,
        out RecognitionRegion frameRegion,
        out string error)
    {
        frameRegion = new RecognitionRegion();
        error = string.Empty;

        _ = TryGetClientAreaInCapturedFrame(
            frame,
            targetWindow,
            out var clientX,
            out var clientY,
            out var sourceWidth,
            out var sourceHeight);

        var sourceRegion = ScaleRegion(region, configWidth, configHeight, sourceWidth, sourceHeight);
        var x = clientX + sourceRegion.X;
        var y = clientY + sourceRegion.Y;
        var right = clientX + sourceRegion.X + sourceRegion.Width;
        var bottom = clientY + sourceRegion.Y + sourceRegion.Height;

        var clampedX = ClampToRange(x, 0, frame.Width);
        var clampedY = ClampToRange(y, 0, frame.Height);
        var clampedRight = ClampToRange(right, 0, frame.Width);
        var clampedBottom = ClampToRange(bottom, 0, frame.Height);
        if (clampedRight <= clampedX || clampedBottom <= clampedY)
        {
            error = $"区域 {region.Id} 不在当前游戏画面内";
            return false;
        }

        frameRegion = new RecognitionRegion
        {
            Id = region.Id,
            X = clampedX,
            Y = clampedY,
            Width = clampedRight - clampedX,
            Height = clampedBottom - clampedY,
            Enabled = region.Enabled
        };
        return true;
    }

    private static async Task<string> SaveRegionScreenshotAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        RecognitionRegion frameRegion)
    {
        Directory.CreateDirectory(RegionScreenshotDirectory);

        var fileName = $"{SanitizeFileName(region.Id)}_{frame.CapturedAt:yyyyMMdd_HHmmss_fff}.png";
        var filePath = Path.Combine(RegionScreenshotDirectory, fileName);
        var pixels = CropFrame(frame, frameRegion);

        await SavePngAsync(filePath, frameRegion.Width, frameRegion.Height, pixels);
        return filePath;
    }

    private static byte[] CropFrame(CapturedFrame frame, RecognitionRegion region)
    {
        var expectedLength = checked(frame.Width * frame.Height * 4);
        if (frame.PixelByteLength < expectedLength)
        {
            throw new InvalidDataException("捕获帧像素数据不完整");
        }

        var rowLength = region.Width * 4;
        var croppedPixels = new byte[region.Height * rowLength];
        for (var row = 0; row < region.Height; row++)
        {
            var sourceOffset = ((region.Y + row) * frame.Width + region.X) * 4;
            var targetOffset = row * rowLength;
            System.Buffer.BlockCopy(frame.Pixels, sourceOffset, croppedPixels, targetOffset, rowLength);
        }

        return croppedPixels;
    }

    private static async Task SavePngAsync(string path, int width, int height, byte[] pixels)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var encodedLength = checked((int)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        _ = await reader.LoadAsync((uint)encodedLength);

        var encodedBytes = new byte[encodedLength];
        reader.ReadBytes(encodedBytes);
        await File.WriteAllBytesAsync(path, encodedBytes);
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Trim().Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var fileName = builder.ToString().Trim(' ', '_');
        return string.IsNullOrWhiteSpace(fileName) ? "region" : fileName;
    }

    private static RecognitionRegion NormalizeRegionToClientArea(
        RecognitionRegion region,
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        out int sourceWidth,
        out int sourceHeight)
    {
        if (!TryGetClientAreaInCapturedFrame(frame, targetWindow, out var clientX, out var clientY, out sourceWidth, out sourceHeight))
        {
            sourceWidth = frame.Width;
            sourceHeight = frame.Height;
            return region;
        }

        var x = ClampToRange(region.X - clientX, 0, Math.Max(0, sourceWidth - 1));
        var y = ClampToRange(region.Y - clientY, 0, Math.Max(0, sourceHeight - 1));
        var right = ClampToRange(region.X + region.Width - clientX, x + 1, sourceWidth);
        var bottom = ClampToRange(region.Y + region.Height - clientY, y + 1, sourceHeight);

        return new RecognitionRegion
        {
            Id = region.Id,
            X = x,
            Y = y,
            Width = right - x,
            Height = bottom - y,
            Enabled = region.Enabled
        };
    }

    private static bool TryGetClientAreaInCapturedFrame(
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        out int clientX,
        out int clientY,
        out int clientWidth,
        out int clientHeight)
    {
        clientX = 0;
        clientY = 0;
        clientWidth = frame.Width;
        clientHeight = frame.Height;

        if (!targetWindow.HasClientArea
            || frame.Width <= 0
            || frame.Height <= 0
            || (frame.Width == targetWindow.ClientWidth && frame.Height == targetWindow.ClientHeight))
        {
            return false;
        }

        clientX = Math.Max(0, targetWindow.ClientOffsetX);
        clientY = Math.Max(0, targetWindow.ClientOffsetY);

        if (clientX >= frame.Width || clientY >= frame.Height)
        {
            return false;
        }

        clientWidth = Math.Min(targetWindow.ClientWidth, frame.Width - clientX);
        clientHeight = Math.Min(targetWindow.ClientHeight, frame.Height - clientY);

        return clientWidth > 0
            && clientHeight > 0
            && (clientX != 0
                || clientY != 0
                || clientWidth != frame.Width
                || clientHeight != frame.Height);
    }

    private static int ClampToRange(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public sealed class EditableRecognitionRegion : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _x = "0";
        private string _y = "0";
        private string _width = "100";
        private string _height = "80";
        private bool _enabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        public string Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        public string Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        public string Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }

        public static EditableRecognitionRegion FromModel(RecognitionRegion region)
        {
            return new EditableRecognitionRegion
            {
                Id = region.Id,
                X = region.X.ToString(),
                Y = region.Y.ToString(),
                Width = region.Width.ToString(),
                Height = region.Height.ToString(),
                Enabled = region.Enabled
            };
        }

        public bool TryToModel(out RecognitionRegion region, out string error)
        {
            region = new RecognitionRegion();
            error = string.Empty;

            var id = Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "ID 不能为空";
                return false;
            }

            if (!TryParseCoordinate(X, "X", allowZero: true, out var x, out error)
                || !TryParseCoordinate(Y, "Y", allowZero: true, out var y, out error)
                || !TryParseCoordinate(Width, "宽", allowZero: false, out var width, out error)
                || !TryParseCoordinate(Height, "高", allowZero: false, out var height, out error))
            {
                return false;
            }

            region = new RecognitionRegion
            {
                Id = id,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Enabled = Enabled
            };
            return true;
        }

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static bool TryParseCoordinate(
            string text,
            string label,
            bool allowZero,
            out int value,
            out string error)
        {
            value = 0;
            error = string.Empty;

            if (!int.TryParse(text, out value))
            {
                error = $"{label} 必须是整数";
                return false;
            }

            if (value < 0 || (!allowZero && value == 0))
            {
                error = allowZero
                    ? $"{label} 不能小于 0"
                    : $"{label} 必须大于 0";
                return false;
            }

            return true;
        }
    }
}
