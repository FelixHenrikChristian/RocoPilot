using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Views.Windows;

namespace RocoPilot.Views.Test;

public sealed partial class ScreenCaptureTestPage : Page
{
    private readonly IWindowEnumerationService _windowEnumerationService;
    private CapturePreviewWindow? _previewWindow;

    public ObservableCollection<CaptureTargetWindow> AvailableWindows
    {
        get;
    } = new();

    public ObservableCollection<CaptureMethodOption> CaptureMethods
    {
        get;
    } = new()
    {
        new(CaptureMethod.BitBlt, "BitBlt", "快速捕获前台可见内容"),
        new(CaptureMethod.PrintWindow, "PrintWindow", "兼容部分后台或被遮挡窗口")
    };

    public ScreenCaptureTestPage()
    {
        _windowEnumerationService = App.GetService<IWindowEnumerationService>();
        InitializeComponent();

        StartPreviewButton.IsEnabled = false;
        CaptureMethodComboBox.ItemsSource = CaptureMethods;
        CaptureMethodComboBox.SelectedIndex = 0;
        WindowComboBox.ItemsSource = AvailableWindows;

        Loaded += ScreenCaptureTestPage_Loaded;
        Unloaded += ScreenCaptureTestPage_Unloaded;
    }

    private void ScreenCaptureTestPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (AvailableWindows.Count == 0)
        {
            RefreshWindowList();
        }
    }

    private void ScreenCaptureTestPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _previewWindow?.Close();
        _previewWindow = null;
    }

    private void RefreshWindowListButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
    }

    private void WindowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedWindowText();
    }

    private void StartPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowComboBox.SelectedItem is not CaptureTargetWindow targetWindow)
        {
            return;
        }

        if (CaptureMethodComboBox.SelectedItem is not CaptureMethodOption captureMethod)
        {
            return;
        }

        _previewWindow?.Close();
        _previewWindow = new CapturePreviewWindow(targetWindow, captureMethod);
        _previewWindow.Closed += (_, _) => _previewWindow = null;
        _previewWindow.Activate();
    }

    private void RefreshWindowList()
    {
        var previousHandle = (WindowComboBox.SelectedItem as CaptureTargetWindow)?.Hwnd;

        AvailableWindows.Clear();
        foreach (var window in _windowEnumerationService.GetVisibleWindows())
        {
            AvailableWindows.Add(window);
        }

        var nextSelection = AvailableWindows.FirstOrDefault(window => window.Hwnd == previousHandle)
            ?? AvailableWindows.FirstOrDefault();

        WindowComboBox.SelectedItem = nextSelection;

        if (AvailableWindows.Count == 0)
        {
            SelectedWindowText.Text = "没有找到可捕获的可见窗口";
        }
    }

    private void UpdateSelectedWindowText()
    {
        if (WindowComboBox.SelectedItem is not CaptureTargetWindow targetWindow)
        {
            SelectedWindowText.Text = "尚未选择窗口";
            StartPreviewButton.IsEnabled = false;
            return;
        }

        SelectedWindowText.Text = $"{targetWindow.DisplayName}  ·  {targetWindow.HandleText}";
        StartPreviewButton.IsEnabled = true;
    }
}
