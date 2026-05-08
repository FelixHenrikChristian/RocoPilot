using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Views.Windows;

namespace RocoPilot.Services;

public sealed class InfoOverlayService : IInfoOverlayService
{
    private readonly ILogger<InfoOverlayService> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private InfoOverlayWindow? _overlayWindow;

    public InfoOverlayService(ILogger<InfoOverlayService> logger)
    {
        _logger = logger;
        _dispatcherQueue = App.MainWindow.DispatcherQueue;
    }

    public void Show(RuntimeTaskState state)
    {
        if (!state.Options.InfoOverlayEnabled)
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

    public void ResetPosition()
    {
        RunOnDispatcher(() => _overlayWindow?.ResetPosition());
    }

    public void SetLocked(bool isLocked)
    {
        RunOnDispatcher(() => _overlayWindow?.SetLocked(isLocked));
    }

    public void UpdateSnapshot(InfoOverlaySnapshot snapshot)
    {
        RunOnDispatcher(() => _overlayWindow?.UpdateSnapshot(snapshot));
    }

    private void ShowCore(RuntimeTaskState state)
    {
        try
        {
            HideCore();

            _overlayWindow = new InfoOverlayWindow(
                state.TargetWindow,
                state.Options.InfoOverlayLocked,
                state.Options.PollutionCounterEnabled,
                state.Options.AutoBattleSettings.IsEnabled);
            _overlayWindow.Closed += (_, _) => _overlayWindow = null;
            _overlayWindow.UpdateSnapshot(InfoOverlaySnapshot.CreateInitial(state.StartedAt));
            _overlayWindow.ShowOverlay();

            _logger.LogDebug("信息遮罩窗口已显示。Locked={Locked}", state.Options.InfoOverlayLocked);
        }
        catch (Exception ex)
        {
            _overlayWindow = null;
            _logger.LogWarning(ex, "显示信息遮罩窗口失败");
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
            _logger.LogDebug(ex, "关闭信息遮罩窗口时发生异常");
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
