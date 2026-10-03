using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Helpers;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Views.Windows;

namespace RocoPilot.Services;

public sealed class InfoOverlayService : IInfoOverlayService, IInfoOverlayNotificationService
{
    private readonly ILogger<InfoOverlayService> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IStatisticsService _statisticsService;
    private readonly ISpiritCatalogService _spiritCatalogService;

    private InfoOverlayWindow? _overlayWindow;
    private InfoOverlayNotice? _uidNotice;

    public InfoOverlayService(
        IStatisticsService statisticsService,
        ISpiritCatalogService spiritCatalogService,
        ILogger<InfoOverlayService> logger)
    {
        _statisticsService = statisticsService;
        _spiritCatalogService = spiritCatalogService;
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

            _ = LoadAvatarPathsAsync(_overlayWindow);

            _logger.LogDebug("信息遮罩窗口已显示。Locked={Locked}", state.Options.InfoOverlayLocked);
        }
        catch (Exception ex)
        {
            _overlayWindow = null;
            _logger.LogWarning(ex, "显示信息遮罩窗口失败");
        }
    }

    private async Task LoadAvatarPathsAsync(InfoOverlayWindow window)
    {
        try
        {
            var catalog = await _spiritCatalogService.LoadAsync();
            // 只读取已有图鉴与本地头像，不发起同步或下载。
            var paths = await Task.Run(() =>
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var spirit in catalog.Spirits)
                {
                    var path = _spiritCatalogService.ResolveAvatarPath(spirit.AvatarPath);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    foreach (var name in new[] { spirit.Name, spirit.WikiName, spirit.BaseName }.Concat(spirit.Aliases))
                    {
                        var key = TextMatchingHelper.NormalizeSpiritNameForMatching(name);
                        if (key.Length > 0) result.TryAdd(key, path);
                    }
                }
                return result;
            });
            RunOnDispatcher(() =>
            {
                if (ReferenceEquals(_overlayWindow, window)) window.SetAvatarPaths(paths);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载信息遮罩精灵头像失败，使用占位图标");
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
