using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;
using RocoPilot.Views.Windows;

namespace RocoPilot.Services;

public sealed class RecognitionOverlayService : IRecognitionOverlayService
{
    private readonly ILogger<RecognitionOverlayService> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private RecognitionOverlayWindow? _overlayWindow;

    public RecognitionOverlayService(ILogger<RecognitionOverlayService> logger)
    {
        _logger = logger;
        _dispatcherQueue = App.MainWindow.DispatcherQueue;
    }

    public void Show(RuntimeTaskState state)
    {
        if (!state.Options.RecognitionOverlayEnabled)
        {
            Hide();
            return;
        }

        RunOnDispatcher(() => ShowCore(state));
    }

    public void Hide()
    {
        RunOnDispatcher(HideCore);
    }

    private void ShowCore(RuntimeTaskState state)
    {
        try
        {
            HideCore();

            _overlayWindow = new RecognitionOverlayWindow(
                state.TargetWindow,
                state.RecognitionRegionConfig);
            _overlayWindow.Closed += (_, _) => _overlayWindow = null;
            _overlayWindow.ShowOverlay();

            _logger.LogDebug(
                "识别区域遮罩已显示，区域数量：{RegionCount}",
                state.RecognitionRegionConfig.Regions.Count(region => region.Enabled));
        }
        catch (Exception ex)
        {
            _overlayWindow = null;
            _logger.LogWarning(ex, "显示识别区域遮罩失败");
        }
    }

    private void HideCore()
    {
        var overlayWindow = _overlayWindow;
        _overlayWindow = null;

        if (overlayWindow is null)
        {
            return;
        }

        try
        {
            overlayWindow.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭识别区域遮罩时发生异常");
        }
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() => action());
    }
}
