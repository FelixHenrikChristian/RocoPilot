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
    private InfoOverlayWindow? _islandWindow;
    private RuntimeTaskState? _shownState;
    private InfoOverlaySnapshot? _lastSnapshot;
    private long _lastRevision;
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
            _islandWindow?.ResetPosition();
        });
    }

    public void SetLocked(bool isLocked)
    {
        RunOnDispatcher(() =>
        {
            _overlayWindow?.SetLocked(isLocked);
            _islandWindow?.SetLocked(isLocked);
        });
    }

    public void UpdateTaskIndicators(bool isEncounterStatisticsEnabled, bool isAutoBattleEnabled)
    {
        RunOnDispatcher(() =>
        {
            if (_lastSnapshot is not null) _lastSnapshot = _lastSnapshot with { IsAutoBattleEnabled = isAutoBattleEnabled };
            _islandWindow?.UpdateTaskIndicators(isEncounterStatisticsEnabled, isAutoBattleEnabled);
        });
    }

    public void UpdateSnapshot(InfoOverlaySnapshot snapshot)
    {
        RunOnDispatcher(() =>
        {
            if (snapshot.SessionStartedAt.HasValue && snapshot.SessionStartedAt != _shownState?.StartedAt) return;
            if (snapshot.Revision != 0 && snapshot.Revision <= _lastRevision) return;
            _lastRevision = snapshot.Revision;
            _lastSnapshot = snapshot;
            if (!_statisticsService.IsActiveAccountSelectionRequired && _uidNotice is not null)
            {
                _uidNotice = null;
                _islandWindow?.UpdateUidNotice(null);
            }

            _overlayWindow?.UpdateSnapshot(snapshot);
            _islandWindow?.UpdateSnapshot(snapshot);
        });
    }

    public void UpdateUidNotice(InfoOverlayNotice? notice)
    {
        RunOnDispatcher(() =>
        {
            _uidNotice = notice;
            _islandWindow?.UpdateUidNotice(notice);
        });
    }

    private void ShowCore(RuntimeTaskState state)
    {
        try
        {
            HideCore();
            if (!ReferenceEquals(_shownState, state))
            {
                _lastSnapshot = InfoOverlaySnapshot.CreateInitial(state.StartedAt);
                _lastRevision = 0;
                _shownState = state;
            }

            _overlayWindow = new InfoOverlayWindow(
                state.TargetWindow,
                state.Options.InfoOverlayLocked,
                state.Options.EncounterStatisticsEnabled,
                state.Options.AutoBattleSettings.IsEnabled);
            var records = _overlayWindow;
            records.Closed += (_, _) => { if (ReferenceEquals(_overlayWindow, records)) _overlayWindow = null; };
            _islandWindow = new InfoOverlayWindow(state.TargetWindow, state.Options.InfoOverlayLocked,
                state.Options.EncounterStatisticsEnabled, state.Options.AutoBattleSettings.IsEnabled, isIsland: true);
            var island = _islandWindow;
            island.Closed += (_, _) => { if (ReferenceEquals(_islandWindow, island)) _islandWindow = null; };
            _overlayWindow.UpdateSnapshot(_lastSnapshot!);
            _islandWindow.UpdateUidNotice(_uidNotice);
            _islandWindow.UpdateSnapshot(_lastSnapshot!);
            _overlayWindow.ShowOverlay();
            _islandWindow.ShowOverlay();
            if (!_overlayWindow.IsExcludedFromCapture || !_islandWindow.IsExcludedFromCapture)
                _logger.LogWarning("信息遮罩未能从系统截图中排除，使用 BitBlt 时请避免把遮罩放在识别区域上方");

            _ = LoadAvatarPathsAsync(_overlayWindow);

            _logger.LogDebug("信息遮罩窗口已显示。Locked={Locked}", state.Options.InfoOverlayLocked);
        }
        catch (Exception ex)
        {
            HideCore();
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
                if (ReferenceEquals(_overlayWindow, window))
                {
                    window.SetAvatarPaths(paths);
                    _islandWindow?.SetAvatarPaths(paths);
                }
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
        var islandWindow = _islandWindow;
        _overlayWindow = null;
        _islandWindow = null;

        try
        {
            try { overlayWindow?.Close(); }
            finally { islandWindow?.Close(); }
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
            ApplyUpdate();
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(ApplyUpdate);

        void ApplyUpdate()
        {
            try { action(); }
            catch (Exception ex) { _logger.LogWarning(ex, "更新信息遮罩失败"); }
        }
    }
}
