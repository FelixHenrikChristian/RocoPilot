using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Logging;
using RocoPilot.Services.Recognition;
using RocoPilot.Services.RuntimeTasks;
using static RocoPilot.Services.RuntimeTasks.RuntimeFrameRecognizer;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService : IRuntimeTaskService, IRuntimeSessionControl
{
    private const int SuspensionPollIntervalMs = 200;
    private static readonly TimeSpan UnrecognizedStateConfirmDelay = TimeSpan.FromSeconds(2);

    private readonly IGameWindowService _gameWindowService;
    private readonly IKeyboardInputService _keyboardInputService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly IImageMatchingService _imageMatchingService;
    private readonly RuntimeFrameRecognizer _frameRecognizer;
    private readonly GameSceneRecognizer _gameScene;
    private readonly BattleScreenRecognizer _battleScreen;
    private readonly AutoBattleInputExecutor _battleInput;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly IStatisticsService _statisticsService;
    private readonly StatisticsUidCoordinatorService _statisticsUidCoordinator;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IRecognitionOverlayService _recognitionOverlayService;
    private readonly IInfoOverlayService _infoOverlayService;
    private readonly ShinyEncounterNotification _shinyNotification;
    private readonly ILogger<RuntimeTaskService> _logger;
    private readonly RecognitionDiagnostics? _recognitionDiagnostics;

    // 循环里的故障每一帧都会重复，同一种只在第一次和之后每隔一段时间记一条。
    private static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromSeconds(30);
    private readonly RepeatedFailureGate _captureFailures = new(RepeatedFailureLogInterval);
    private readonly RepeatedFailureGate _sceneFailures = new(RepeatedFailureLogInterval);
    private readonly RepeatedFailureGate _battleOcrFailures = new(RepeatedFailureLogInterval);
    private readonly RepeatedFailureGate _battleInputFailures = new(RepeatedFailureLogInterval);
    private readonly RepeatedFailureGate _overlayFailures = new(RepeatedFailureLogInterval);
    private readonly RepeatedFailureGate _spiritNameFailures = new(RepeatedFailureLogInterval);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    private RuntimeSession? _session;
    private int _queuedAutoBattleSkillFailureTipRecognition;
    private bool _settingsLoaded;
    private bool _infoOverlayEncounterPanelEnabled = true;
    private volatile GameScene _scene;
    private DateTimeOffset? _unrecognizedStateDetectedAt;
    private volatile bool _isSuspended;
    private string? _suspendedReason;

    // 当前这场战斗的开始时间，只在截图循环里读写；为空表示不在战斗中。
    private DateTimeOffset? _battleStartedAt;
    private readonly object _battleSpiritLogLock = new();
    private long _loggedSpiritBattleId = -1;
    private string _loggedSpiritName = string.Empty;
    private readonly SemaphoreSlim _runtimeIteration = new(1, 1);

    public event EventHandler? SettingsChanged;

    public RuntimeTaskState? CurrentState => _session?.State;

    public bool IsRunning => CurrentState is not null;

    public bool IsSuspended => _isSuspended;

    private void Suspend(string reason)
    {
        if (_isSuspended)
        {
            return;
        }

        _suspendedReason = reason;
        _isSuspended = true;
        _recognitionOverlayService.Hide();
        if (IsRunning)
        {
            _logger.LogInformation("实时任务：已挂起（{Reason}），截图与识别暂停", reason);
        }
    }

    public async Task SuspendAsync(string reason, CancellationToken cancellationToken = default)
    {
        Suspend(reason);
        await _runtimeIteration.WaitAsync(cancellationToken);
        try
        {
            if (_session is { } session) await session.DrainBackgroundAsync(cancellationToken);
            _recognitionOverlayService.Hide();
        }
        finally { _runtimeIteration.Release(); }
    }

    public void Resume()
    {
        if (!_isSuspended)
        {
            return;
        }

        var reason = _suspendedReason;
        _suspendedReason = null;
        _isSuspended = false;
        if (IsRunning)
        {
            _logger.LogInformation("实时任务：已恢复运行（挂起原因：{Reason}）", reason);
        }
    }

    public RuntimeTaskService(
        IGameWindowService gameWindowService,
        IKeyboardInputService keyboardInputService,
        IScreenCaptureService screenCaptureService,
        IRecognitionRegionConfigService recognitionRegionConfigService,
        IImageMatchingService imageMatchingService,
        RuntimeFrameRecognizer frameRecognizer,
        GameSceneRecognizer gameScene,
        BattleScreenRecognizer battleScreen,
        AutoBattleInputExecutor battleInput,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        ISpiritCatalogService spiritCatalogService,
        IStatisticsService statisticsService,
        StatisticsUidCoordinatorService statisticsUidCoordinator,
        ILocalSettingsService localSettingsService,
        IHotkeyService hotkeyService,
        IRecognitionOverlayService recognitionOverlayService,
        IInfoOverlayService infoOverlayService,
        ShinyEncounterNotification shinyNotification,
        ILogger<RuntimeTaskService> logger,
        RecognitionDiagnostics? recognitionDiagnostics = null)
    {
        _gameWindowService = gameWindowService;
        _keyboardInputService = keyboardInputService;
        _screenCaptureService = screenCaptureService;
        _recognitionRegionConfigService = recognitionRegionConfigService;
        _imageMatchingService = imageMatchingService;
        _frameRecognizer = frameRecognizer;
        _gameScene = gameScene;
        _battleScreen = battleScreen;
        _battleInput = battleInput;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _spiritCatalogService = spiritCatalogService;
        _statisticsService = statisticsService;
        _statisticsUidCoordinator = statisticsUidCoordinator;
        _localSettingsService = localSettingsService;
        _hotkeyService = hotkeyService;
        _hotkeyService.HotkeyTriggered += HotkeyService_HotkeyTriggered;
        _recognitionOverlayService = recognitionOverlayService;
        _infoOverlayService = infoOverlayService;
        _shinyNotification = shinyNotification;
        _logger = logger;
        _recognitionDiagnostics = recognitionDiagnostics;
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            if (_settingsLoaded)
            {
                return;
            }

            var savedEncounterStatisticsEnabled =
                await _localSettingsService.ReadSettingAsync<bool?>(SettingsKeys.EncounterStatisticsEnabled);
            _encounterStatisticsEnabled = savedEncounterStatisticsEnabled ?? true;

            var savedInfoOverlayEncounterPanelEnabled =
                await _localSettingsService.ReadSettingAsync<bool?>(SettingsKeys.InfoOverlayEncounterPanelEnabled);
            _infoOverlayEncounterPanelEnabled = savedInfoOverlayEncounterPanelEnabled ?? true;

            var savedAutoBattleSettings =
                await _localSettingsService.ReadSettingAsync<AutoBattleSettings>(SettingsKeys.AutoBattleSettings);
            _autoBattleSettings = AutoBattleSettingsRules.Normalize(savedAutoBattleSettings);

            await _hotkeyService.LoadSettingsAsync(cancellationToken);
            _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            _settingsLoaded = true;
            _logger.LogWarning(ex, "读取实时任务设置失败，已使用默认设置");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<RuntimeTaskStartResult> StartAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken = default)
    {
        await LoadSettingsAsync(cancellationToken);
        await _statisticsService.LoadAsync();

        await _lifecycleLock.WaitAsync(cancellationToken);
        CaptureTargetWindow? preparingWindow = null;
        try
        {
            if (CurrentState is not null)
            {
                return RuntimeTaskStartResult.Started(CurrentState);
            }

            var autoBattleSettings = AutoBattleSettingsRules.Normalize(options.AutoBattleSettings);
            var targetWindow = _gameWindowService.FindGameWindow();
            if (targetWindow is null)
            {
                var missingWindowMessage = $"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}";
                _logger.LogError("实时任务启动失败：未找到游戏窗口（进程 {ProcessName}）",
                    _gameWindowService.TargetProcessName);
                return RuntimeTaskStartResult.Failed(missingWindowMessage);
            }

            if (autoBattleSettings.IsEnabled)
            {
                // 初始化驱动和键盘设备可能阻塞；启动前检查不发送任何按键。
                await Task.Run(() => _keyboardInputService.EnsureReady(autoBattleSettings.KeyboardInputMethod), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var shouldBringGameWindowToForeground =
                ShouldBringGameWindowToForegroundOnStart(autoBattleSettings);
            var broughtGameWindowToForeground = false;
            if (shouldBringGameWindowToForeground)
            {
                broughtGameWindowToForeground = _gameWindowService.TryBringGameWindowToForeground(targetWindow);
                if (broughtGameWindowToForeground)
                {
                    _logger.LogDebug(
                        "实时任务启动：已将游戏窗口切换到前台，输入方式 {InputMethod}，窗口 {Window}",
                        autoBattleSettings.KeyboardInputMethod,
                        targetWindow.DisplayName);
                }
                else
                {
                    _logger.LogWarning(
                        "实时任务：未能把游戏窗口切换到前台，自动战斗的按键可能发不出去，请手动切到游戏（输入方式 {InputMethod}）",
                        autoBattleSettings.KeyboardInputMethod);
                }
            }

            preparingWindow = targetWindow;
            using var firstFrame = await CaptureFrameAsync(targetWindow, options.CaptureMethod, cancellationToken);
            if (firstFrame is null)
            {
                _screenCaptureService.Release(targetWindow, options.CaptureMethod);
                var captureFailedMessage = $"找到窗口，但未能获取画面：{targetWindow.DisplayName}";
                _logger.LogError("实时任务启动失败：找到了游戏窗口，但获取不到画面（{Window}，截图方式 {CaptureMethod}）",
                    targetWindow.DisplayName, options.CaptureMethod);
                return RuntimeTaskStartResult.Failed(captureFailedMessage);
            }

            var configResolutionWidth = targetWindow.HasClientArea
                ? targetWindow.ClientWidth
                : firstFrame.Width;
            var configResolutionHeight = targetWindow.HasClientArea
                ? targetWindow.ClientHeight
                : firstFrame.Height;
            var detectedAspectRatio = configResolutionHeight > 0
                ? configResolutionWidth / (double)configResolutionHeight
                : 0d;
            var (detectedClientOffsetX, detectedClientOffsetY) = targetWindow.GetClientOffsetForFrame(
                firstFrame.Width,
                firstFrame.Height);
            _logger.LogDebug(
                "检测到游戏分辨率：Client={ClientWidth}x{ClientHeight}, AspectRatio={AspectRatio:F6}, Window={WindowWidth}x{WindowHeight}, DwmFrame={DwmWidth}x{DwmHeight}, FirstFrame={FrameWidth}x{FrameHeight}, ClientOffset={ClientOffsetX},{ClientOffsetY}, CaptureMethod={CaptureMethod}",
                configResolutionWidth,
                configResolutionHeight,
                detectedAspectRatio,
                targetWindow.Width,
                targetWindow.Height,
                targetWindow.ExtendedFrameWidth,
                targetWindow.ExtendedFrameHeight,
                firstFrame.Width,
                firstFrame.Height,
                detectedClientOffsetX,
                detectedClientOffsetY,
                options.CaptureMethod);

            if (!_recognitionRegionConfigService.TryResolveConfigResolution(
                configResolutionWidth,
                configResolutionHeight,
                out var matchedConfigWidth,
                out var matchedConfigHeight))
            {
                _screenCaptureService.Release(targetWindow, options.CaptureMethod);
                var unsupportedResolutionMessage =
                    $"不支持的游戏分辨率：{configResolutionWidth}x{configResolutionHeight}（宽高比 {detectedAspectRatio:F4}）。当前仅支持 16:9 和 4:3。";
                _logger.LogError(
                    "实时任务启动失败：不支持的游戏分辨率 {Width}x{Height}，当前仅支持 16:9 和 4:3",
                    configResolutionWidth, configResolutionHeight);
                return RuntimeTaskStartResult.Failed(unsupportedResolutionMessage);
            }

            var recognitionRegionConfig = _recognitionRegionConfigService.LoadForResolution(
                configResolutionWidth,
                configResolutionHeight);
            if (!recognitionRegionConfig.LoadedFromFile
                || !recognitionRegionConfig.Regions.Any(region => region.Enabled))
            {
                _screenCaptureService.Release(targetWindow, options.CaptureMethod);
                var missingConfigMessage =
                    $"未能加载 {matchedConfigWidth}x{matchedConfigHeight} 识别配置：{recognitionRegionConfig.SourcePath}";
                _logger.LogError("实时任务启动失败：未能加载 {Width}x{Height} 的识别配置（{ConfigPath}）",
                    matchedConfigWidth, matchedConfigHeight, recognitionRegionConfig.SourcePath);
                return RuntimeTaskStartResult.Failed(missingConfigMessage);
            }

            var markerTemplate = GetResolutionTemplatePath(recognitionRegionConfig, CaptureButtonDisabledMarkerTemplateName);
            if (!_frameRecognizer.TemplateExists(markerTemplate))
                _logger.LogWarning("缺少捕捉按钮模板，本次运行无法识别奇遇效果解除：{Template}", markerTemplate);

            options.InfoOverlayEncounterPanelEnabled = InfoOverlayEncounterPanelEnabled;
            var state = new RuntimeTaskState(
                targetWindow,
                recognitionRegionConfig,
                options,
                DateTimeOffset.Now);
            var session = new RuntimeSession(state, _screenCaptureService);
            _session = session;
            preparingWindow = null;
            _scene = GameScene.Unknown;
            _unrecognizedStateDetectedAt = null;
            _isSuspended = false;
            _suspendedReason = null;
            _battleStartedAt = null;
            _captureFailures.Reset();
            _sceneFailures.Reset();
            _battleOcrFailures.Reset();
            _battleInputFailures.Reset();
            _overlayFailures.Reset();
            _spiritNameFailures.Reset();
            ResetAutoBattleBattleState();
            _overlayActivities.Clear();
            Volatile.Write(ref _lastInfoOverlayStatus, "状态待识别");
            ResetEncounterRecordSuppression();
            _encounterStatisticsEnabled = options.EncounterStatisticsEnabled;
            _autoBattleSettings = autoBattleSettings;
            _recognitionOverlayService.Show(state);
            _infoOverlayService.Show(state);
            UpdateInfoOverlayTaskIndicators();
            session.Start(CaptureLoopAsync, RuntimeOcrLoopAsync);

            _logger.LogInformation(
                "实时任务已启动：{Window}，{ClientWidth}×{ClientHeight}，自动战斗{AutoBattle}，奇遇统计{Statistics}",
                targetWindow.DisplayName,
                configResolutionWidth,
                configResolutionHeight,
                autoBattleSettings.IsEnabled ? "已开启" : "未开启",
                options.EncounterStatisticsEnabled ? "已开启" : "未开启");

            _logger.LogDebug(
                "实时任务参数：截图 {CaptureMethod}，OCR {TextRecognitionMethod}，匹配 {ImageMatchAlgorithm}，输入 {InputMethod}，首帧 {FrameWidth}x{FrameHeight}，启用识别区域 {EnabledRegionCount} 个，配置 {ConfigPath}",
                options.CaptureMethod,
                options.TextRecognitionMethod,
                _imageMatchingService.DefaultAlgorithm,
                autoBattleSettings.KeyboardInputMethod,
                firstFrame.Width,
                firstFrame.Height,
                recognitionRegionConfig.Regions.Count(region => region.Enabled),
                recognitionRegionConfig.SourcePath);

            var message = shouldBringGameWindowToForeground
                ? broughtGameWindowToForeground
                    ? "实时任务已启动，已将游戏窗口切换到前台。"
                    : "实时任务已启动，但未能自动切换游戏窗口到前台。"
                : "实时任务已启动。";

            return RuntimeTaskStartResult.Started(state, message);
        }
        catch (OperationCanceledException)
        {
            if (preparingWindow is not null) _screenCaptureService.Release(preparingWindow, options.CaptureMethod);
            await StopSessionCoreAsync();
            return RuntimeTaskStartResult.Failed("启动任务已取消。");
        }
        catch (Exception ex)
        {
            if (preparingWindow is not null) _screenCaptureService.Release(preparingWindow, options.CaptureMethod);
            await StopSessionCoreAsync();
            _logger.LogError(ex, "实时任务启动失败");
            return RuntimeTaskStartResult.Failed($"启动失败：{ex.Message}");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private bool ShouldBringGameWindowToForegroundOnStart(AutoBattleSettings settings)
    {
        return settings.IsEnabled
            && _keyboardInputService.RequiresForeground(settings.KeyboardInputMethod);
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try { await StopSessionCoreAsync(); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StopSessionCoreAsync()
    {
        _statisticsUidCoordinator.Clear();
        var session = _session;
        if (session is null) return;
        try
        {
            try
            {
                _recognitionOverlayService.Hide();
                _infoOverlayService.Hide();
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            _session = null;
            // 等待后台任务退出期间仍可能收到开关热键，收尾时一并使其 UID 请求失效。
            _statisticsUidCoordinator.Clear();
            ResetAutoBattleBattleState();
            _overlayActivities.Clear();
            _battleStartedAt = null;
            // 写出最后一段识别汇总，下次启动时各识别目标从头记录。
            _recognitionDiagnostics?.Reset();
            _logger.LogInformation("实时任务：已停止");
        }
    }

    private async Task CaptureLoopAsync(RuntimeSession session, CancellationToken cancellationToken)
    {
        var state = session.State;
        var nextGameStateScanAt = DateTimeOffset.MinValue;
        var wasSuspended = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_isSuspended)
                {
                    if (!wasSuspended)
                    {
                        wasSuspended = true;
                        EnterCaptureLoopSuspension(session);
                    }

                    await DelayAsync(SuspensionPollIntervalMs, cancellationToken);
                    continue;
                }

                var frameStart = Stopwatch.GetTimestamp();
                CapturedFrame? frame = null;

                await _runtimeIteration.WaitAsync(cancellationToken);
                try
                {
                    if (_isSuspended) continue;
                    if (wasSuspended)
                    {
                        wasSuspended = false;
                        ExitCaptureLoopSuspension(state);
                    }
                    try
                    {
                        frame = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                        NoteCaptureResult(state, frame is not null, null);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        NoteCaptureResult(state, false, ex);
                        await DelayAsync(500, cancellationToken);
                    }

                    try
                    {
                        if (frame is not null)
                        {
                            var now = DateTimeOffset.Now;
                            if (now >= nextGameStateScanAt)
                            {
                                nextGameStateScanAt = now + TimeSpan.FromMilliseconds(RuntimeRecognitionDefaults.GameStateScanIntervalMs);

                                try
                                {
                                    await ProcessFrameAsync(state, frame, cancellationToken);
                                    _sceneFailures.Reset();
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    _scene = GameScene.Unknown;
                                    CompleteAutoBattleSkillSelectionState();
                                    TryUpdateUnrecognizedInfoOverlaySnapshot(now);
                                    if (_sceneFailures.TryEnter(out var skipped))
                                    {
                                        _logger.LogWarning(ex, "实时任务：画面识别出错，本帧按未识别处理{Repeat}",
                                            RepeatedFailureGate.Describe(skipped));
                                    }
                                }

                                // 后台战斗 OCR 只接收本轮已确认的战斗画面。
                                if (_scene == GameScene.Battle)
                                    session.PublishFrame(frame, _battle.BattleId);
                                else
                                    session.ClearFrame();
                            }
                        }
                    }
                    finally
                    {
                        frame?.Dispose();
                    }
                }
                finally { _runtimeIteration.Release(); }

                var elapsedMilliseconds = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                var delay = Math.Max(1, RuntimeRecognitionDefaults.FrameCaptureIntervalMs - (int)elapsedMilliseconds);
                await DelayAsync(delay, cancellationToken);
            }
        }
        finally
        {
            session.ClearFrame();
        }
    }

    // 挂起清理在截图循环线程内执行，避免与扫描逻辑并发修改战斗状态。
    private void EnterCaptureLoopSuspension(RuntimeSession session)
    {
        _scene = GameScene.Unknown;
        _unrecognizedStateDetectedAt = null;
        _battleStartedAt = null;
        ResetAutoBattleBattleState();
        ResetEncounterRecordSuppression();
        session.ClearFrame();
        _overlayActivities.Clear();
        _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot("已挂起", DateTimeOffset.Now));
        _recognitionDiagnostics?.Flush();
        _logger.LogDebug("实时任务：截图循环进入挂起状态");
    }

    private void ExitCaptureLoopSuspension(RuntimeTaskState state)
    {
        if (state.Options.RecognitionOverlayEnabled)
        {
            _recognitionOverlayService.Show(state);
        }

        _logger.LogDebug("实时任务：截图循环退出挂起状态，重新开始识别");
        _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot("状态待识别", DateTimeOffset.Now));
    }

    private Task RuntimeOcrLoopAsync(RuntimeSession session, CancellationToken cancellationToken) =>
        Task.WhenAll(
            RuntimeOcrLoopAsync(session, true, cancellationToken),
            RuntimeOcrLoopAsync(session, false, cancellationToken));

    private async Task RuntimeOcrLoopAsync(RuntimeSession session, bool shinyOnly, CancellationToken cancellationToken)
    {
        var state = session.State;
        Task? activeScanTask = null;
        DateTimeOffset? lastFrameAt = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(shinyOnly ? RuntimeRecognitionDefaults.ShinyScanIntervalMs : RuntimeRecognitionDefaults.OcrScanIntervalMs, cancellationToken);

                if (activeScanTask is { IsCompleted: false }) continue;
                if (activeScanTask is not null) await activeScanTask;
                activeScanTask = null;

                if (_isSuspended || _scene != GameScene.Battle
                    || (shinyOnly && _battle.IsSuspendedForShiny))
                {
                    continue;
                }

                await _runtimeIteration.WaitAsync(cancellationToken);
                try
                {
                    if (_isSuspended) continue;
                    var battleId = _battle.BattleId;
                    var frame = session.RentFrame(battleId);
                    if (frame is null) continue;
                    if (shinyOnly && frame.CapturedAt == lastFrameAt)
                    {
                        frame.Dispose();
                        continue;
                    }
                    lastFrameAt = frame.CapturedAt;
                    activeScanTask = RunRuntimeOcrScanAsync(state, frame, battleId, shinyOnly, cancellationToken);
                    session.Track(activeScanTask);
                }
                finally { _runtimeIteration.Release(); }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (activeScanTask is not null) await activeScanTask;
        }
    }

    private async Task RunRuntimeOcrScanAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        long battleId,
        bool shinyOnly,
        CancellationToken cancellationToken)
    {
        using (frame)
        {
            try
            {
                if (shinyOnly)
                    await TryUpdateRuntimeShinyTipSignalAsync(state, frame, _encounterSeasonConfigService.GetCurrentSeason(), battleId, cancellationToken);
                else
                    await UpdateRuntimeEncounterOcrSignalsAsync(state, frame, battleId, cancellationToken);
                _battleOcrFailures.Reset();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // 这里每秒执行数次，同一种故障不逐次记录。
                if (_battleOcrFailures.TryEnter(out var skipped))
                {
                    _logger.LogWarning(ex, "实时任务：战斗画面的文字识别出错，稍后重试（OCR {Method}）{Repeat}",
                        state.Options.TextRecognitionMethod, RepeatedFailureGate.Describe(skipped));
                }
            }
        }
    }

    private async Task ProcessFrameAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var match = await _gameScene.RecognizeAsync(state, frame, cancellationToken);
        if (_scene != match.Scene) _logger.LogDebug("游戏场景变化：{Previous} → {Current}", _scene, match.Scene);
        _scene = match.Scene;
        switch (match.Scene)
        {
            case GameScene.World:
                NoteBattleEnded();
                ResetAutoBattleBattleState();
                ResetEncounterRecordSuppression();
                UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                    "大世界", DateTimeOffset.Now, match.MagicPointCount));
                break;
            case GameScene.Battle:
                NoteBattleStarted();
                await ProcessBattleFrameAsync(state, frame, match.BattleScreen!.Value, cancellationToken);
                break;
            default:
                CacheS5OpeningTip(state, frame);
                // 无命中只暂停本帧操作，确认大世界后才结束本场战斗。
                CompleteAutoBattleSkillSelectionState();
                _battle.ObservePetSwitching(false);
                TryUpdateUnrecognizedInfoOverlaySnapshot(DateTimeOffset.Now);
                break;
        }
    }

    // 战斗的开始和结束只在这两处记录。战斗中途识别不到画面（动画、过场）不算结束，回到大世界才算。
    private void NoteBattleStarted()
    {
        if (_battleStartedAt is not null)
        {
            return;
        }

        _battleStartedAt = DateTimeOffset.Now;
        _logger.LogInformation("进入战斗");
    }

    private void NoteBattleEnded()
    {
        if (_battleStartedAt is not { } startedAt)
        {
            return;
        }

        _battleStartedAt = null;
        _logger.LogInformation("战斗结束，回到大世界：共 {Turns} 回合，用时 {Seconds:F0} 秒",
            _battle.TurnNumber, (DateTimeOffset.Now - startedAt).TotalSeconds);
    }

    // 同一场战斗里同一只精灵只记一次；后台 OCR 每秒都会识别到名字。
    private void NoteBattleSpirit(long battleId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || battleId != _battle.BattleId)
        {
            return;
        }

        lock (_battleSpiritLogLock)
        {
            if (_loggedSpiritBattleId == battleId && string.Equals(_loggedSpiritName, name, StringComparison.Ordinal))
            {
                return;
            }

            _loggedSpiritBattleId = battleId;
            _loggedSpiritName = name;
        }

        _logger.LogInformation("遇到精灵：{SpiritName}", name);
    }

    private void NoteCaptureResult(RuntimeTaskState state, bool succeeded, Exception? exception)
    {
        if (succeeded)
        {
            var failures = _captureFailures.Reset();
            if (failures > 0)
            {
                _logger.LogInformation("实时任务：已重新获取到游戏画面（此前失败 {Count} 次）", failures);
            }

            return;
        }

        if (_captureFailures.TryEnter(out var skipped))
        {
            _logger.LogWarning(exception,
                "实时任务：获取不到游戏画面，识别暂停并持续重试（{Window}，截图方式 {Method}）{Repeat}",
                state.TargetWindow.DisplayName, state.Options.CaptureMethod, RepeatedFailureGate.Describe(skipped));
        }
    }

    private async Task ProcessBattleFrameAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        BattleScreen screen,
        CancellationToken cancellationToken)
    {
        await UpdateEncounterCaptureButtonStateAsync(state, frame, screen, cancellationToken);
        // 先更新当前状态，长按键序列执行期间也能显示正确的状态和动作。
        Volatile.Write(ref _lastInfoOverlayStatus, screen switch
        {
            BattleScreen.SkillSelection => "战斗中 - 技能选择",
            BattleScreen.PetSwitching => "战斗中 - 切换精灵",
            _ => "战斗中"
        });
        if (screen != BattleScreen.PetSwitching) _battle.ObservePetSwitching(false);

        switch (screen)
        {
            case BattleScreen.SkillSelection:
                var recovered = !_battle.IsSuspendedForShiny
                    && await TryHandleAutoBattleSkillReleaseFailureAsync(state, frame, cancellationToken);
                if (!recovered) await HandleAutoBattleSkillSelectionAsync(state, cancellationToken);
                break;
            case BattleScreen.PetSwitching:
                CompleteAutoBattleSkillSelectionState();
                if (!_battle.IsSuspendedForShiny) await HandleAutoBattlePetSwitchingAsync(state, cancellationToken);
                break;
            case BattleScreen.Chat:
                if (!_battle.IsSuspendedForShiny) CompleteAutoBattleSkillSelectionState();
                break;
        }

        var description = screen switch
        {
            _ when _battle.IsSuspendedForShiny => "战斗中 - 异色保护",
            BattleScreen.SkillSelection => "战斗中 - 技能选择",
            BattleScreen.PetSwitching => "战斗中 - 切换精灵",
            _ => "战斗中"
        };
        UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(description, DateTimeOffset.Now));
    }

    private void UpdateRecognizedInfoOverlaySnapshot(InfoOverlaySnapshot snapshot)
    {
        _unrecognizedStateDetectedAt = null;
        Volatile.Write(ref _lastInfoOverlayStatus, snapshot.StatusText);
        _infoOverlayService.UpdateSnapshot(snapshot);
    }

    private void TryUpdateUnrecognizedInfoOverlaySnapshot(DateTimeOffset now)
    {
        if (_unrecognizedStateDetectedAt is null)
        {
            _unrecognizedStateDetectedAt = now;
            _overlayActivities.Clear();
            Volatile.Write(ref _lastInfoOverlayStatus, "状态待识别");
            _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot("状态待识别", now));
        }

        if (now - _unrecognizedStateDetectedAt.Value < UnrecognizedStateConfirmDelay)
        {
            return;
        }

        Volatile.Write(ref _lastInfoOverlayStatus, "未识别");
        _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot("未识别", now));
    }

    private InfoOverlaySnapshot CreateInfoOverlaySnapshot(
        string statusText,
        DateTimeOffset updatedAt,
        int? magicPointCount = null,
        int magicPointMaximum = GameSceneRecognizer.MagicPointSlotCount)
    {
        var independentTask = Volatile.Read(ref _independentTaskOverlay);
        return new InfoOverlaySnapshot(
            statusText,
            GetCurrentSeasonEncounterCounters(),
            updatedAt,
            magicPointCount,
            magicPointMaximum,
            GetCurrentPendingShinyCapture(),
            _overlayActivities.Current,
            _scene == GameScene.Battle && _battle.IsSuspendedForShiny,
            Interlocked.Increment(ref _infoOverlayRevision),
            CurrentState?.StartedAt,
            _isSuspended ? InfoOverlayScene.Suspended : _scene switch
            {
                GameScene.World => InfoOverlayScene.World,
                GameScene.Battle => InfoOverlayScene.Battle,
                _ => InfoOverlayScene.Unknown
            },
            _autoBattleSettings.IsEnabled,
            _overlayActivities.CreatureName,
            independentTask?.Name ?? string.Empty,
            independentTask?.Progress);
    }

    private Task<CapturedFrame?> CaptureFrameAsync(
        CaptureTargetWindow targetWindow,
        CaptureMethod captureMethod,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => _screenCaptureService.Capture(targetWindow, captureMethod), cancellationToken);
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void NotifySettingsChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
