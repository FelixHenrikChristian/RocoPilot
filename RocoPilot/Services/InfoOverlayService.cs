using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Views.Windows;

namespace RocoPilot.Services;

public sealed class InfoOverlayService : IInfoOverlayService, IInfoOverlayNotificationService
{
    private readonly ILogger<InfoOverlayService> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IStatisticsService _statisticsService;

    private InfoOverlayWindow? _overlayWindow;
    private InfoOverlayNotice? _uidNotice;

    public InfoOverlayService(
        IStatisticsService statisticsService,
        ILogger<InfoOverlayService> logger)
    {
        _statisticsService = statisticsService;
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
        RunOnDispatcher(() =>
        {
            _overlayWindow?.ResetPosition();
            _overlayWindow?.RefreshTopNoticeLayout();
        });
    }

    public void SetLocked(bool isLocked)
    {
        RunOnDispatcher(() =>
        {
            _overlayWindow?.SetLocked(isLocked);
            _overlayWindow?.RefreshTopNoticeLayout();
        });
    }

    public void UpdateTaskIndicators(bool isEncounterStatisticsEnabled, bool isAutoBattleEnabled)
    {
        RunOnDispatcher(() => _overlayWindow?.UpdateTaskIndicators(isEncounterStatisticsEnabled, isAutoBattleEnabled));
    }

    public void UpdateSnapshot(InfoOverlaySnapshot snapshot)
    {
        RunOnDispatcher(() =>
        {
            if (!_statisticsService.IsActiveAccountSelectionRequired && _uidNotice is not null)
            {
                _uidNotice = null;
                _overlayWindow?.UpdateUidNotice(null);
            }

            _overlayWindow?.UpdateSnapshotWithTopNotices(snapshot);
        });
    }

    public void UpdateUidNotice(InfoOverlayNotice? notice)
    {
        RunOnDispatcher(() =>
        {
            _uidNotice = notice;
            _overlayWindow?.UpdateUidNotice(notice);
        });
    }

    private void ShowCore(RuntimeTaskState state)
    {
        try
        {
            HideCore();

            _overlayWindow = new InfoOverlayWindow(
                state.TargetWindow,
                state.Options.InfoOverlayLocked,
                state.Options.EncounterStatisticsEnabled,
                state.Options.AutoBattleSettings.IsEnabled);
            _overlayWindow.Closed += (_, _) => _overlayWindow = null;
            _overlayWindow.InitializeTopNoticeLayout();
            _overlayWindow.UpdateUidNotice(_uidNotice);
            _overlayWindow.UpdateSnapshotWithTopNotices(InfoOverlaySnapshot.CreateInitial(state.StartedAt));
            _overlayWindow.ShowOverlay();
            _overlayWindow.RefreshTopNoticeLayout();

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
