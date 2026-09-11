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
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;
using RocoPilot.Services.RuntimeTasks;
using static RocoPilot.Services.RuntimeTasks.RuntimeDebugLogger;
using static RocoPilot.Services.RuntimeTasks.RuntimeFrameRecognizer;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService : IRuntimeTaskService, IRuntimeSessionControl
{
    private const int MagicPointSlotCount = 6;
    private const string MagicPointTemplateName = "magic-point.png";
    private const int SuspensionPollIntervalMs = 200;
    private static readonly TimeSpan UnrecognizedStateConfirmDelay = TimeSpan.FromSeconds(2);
    private static readonly string[] MagicPointRegionIds =
    [
        RecognitionRegionIds.MagicPoint
    ];
    private static readonly ImageMatchOptions MagicPointMatchOptions = new()
    {
        MinimumScore = 0.96,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    private readonly IGameWindowService _gameWindowService;
    private readonly IKeyboardInputService _keyboardInputService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly IImageMatchingService _imageMatchingService;
    private readonly RuntimeFrameRecognizer _frameRecognizer;
    private readonly BattleScreenRecognizer _battleScreen;
    private readonly AutoBattleInputExecutor _battleInput;
    private readonly RuntimeDebugLogger _debugLog;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly IStatisticsService _statisticsService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IRecognitionOverlayService _recognitionOverlayService;
    private readonly IInfoOverlayService _infoOverlayService;
    private readonly ILogger<RuntimeTaskService> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    private RuntimeSession? _session;
    private RuntimeRecognitionSettings _runtimeRecognitionSettings = RuntimeRecognitionSettings.CreateDefault();
    private int _queuedAutoBattleSkillFailureTipRecognition;
    private bool _settingsLoaded;
    private bool _isBattleStateActive;
    private DateTimeOffset? _unrecognizedStateDetectedAt;
    private volatile bool _isSuspended;
    private string? _suspendedReason;

    public event EventHandler? SettingsChanged;

    public RuntimeTaskState? CurrentState => _session?.State;

    public bool IsRunning => CurrentState is not null;

    public bool IsSuspended => _isSuspended;

    public void Suspend(string reason)
    {
        if (_isSuspended)
        {
            return;
        }

        _suspendedReason = reason;
        _isSuspended = true;
        if (IsRunning)
        {
            _logger.LogInformation("实时任务：已挂起（{Reason}），截图与识别暂停。", reason);
        }
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
            _logger.LogInformation("实时任务：已恢复运行（{Reason}）。", reason);
        }
    }

    public RuntimeRecognitionSettings RuntimeRecognitionSettings =>
        Volatile.Read(ref _runtimeRecognitionSettings).Clone();

    public RuntimeTaskService(
        IGameWindowService gameWindowService,
        IKeyboardInputService keyboardInputService,
        IScreenCaptureService screenCaptureService,
        IRecognitionRegionConfigService recognitionRegionConfigService,
        IImageMatchingService imageMatchingService,
        RuntimeFrameRecognizer frameRecognizer,
        BattleScreenRecognizer battleScreen,
        AutoBattleInputExecutor battleInput,
        RuntimeDebugLogger debugLog,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        ISpiritCatalogService spiritCatalogService,
        IStatisticsService statisticsService,
        ILocalSettingsService localSettingsService,
        IHotkeyService hotkeyService,
        IRecognitionOverlayService recognitionOverlayService,
        IInfoOverlayService infoOverlayService,
        ILogger<RuntimeTaskService> logger)
    {
        _gameWindowService = gameWindowService;
        _keyboardInputService = keyboardInputService;
        _screenCaptureService = screenCaptureService;
        _recognitionRegionConfigService = recognitionRegionConfigService;
        _imageMatchingService = imageMatchingService;
        _frameRecognizer = frameRecognizer;
        _battleScreen = battleScreen;
        _battleInput = battleInput;
        _debugLog = debugLog;
        _encounterSeasonConfigService = encounterSeasonConfigService;
        _spiritCatalogService = spiritCatalogService;
        _statisticsService = statisticsService;
        _localSettingsService = localSettingsService;
        _hotkeyService = hotkeyService;
        _hotkeyService.HotkeyTriggered += HotkeyService_HotkeyTriggered;
        _recognitionOverlayService = recognitionOverlayService;
        _infoOverlayService = infoOverlayService;
        _logger = logger;
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

            var savedAutoBattleSettings =
                await _localSettingsService.ReadSettingAsync<AutoBattleSettings>(SettingsKeys.AutoBattleSettings);
            _autoBattleSettings = AutoBattleSettingsRules.Normalize(savedAutoBattleSettings);

            var savedRuntimeRecognitionSettings =
                await _localSettingsService.ReadSettingAsync<RuntimeRecognitionSettings>(SettingsKeys.RuntimeRecognitionSettings);
            _runtimeRecognitionSettings = NormalizeRuntimeRecognitionSettings(savedRuntimeRecognitionSettings);
            await _imageMatchingService.InitializeAsync(cancellationToken);
            await _hotkeyService.LoadSettingsAsync(cancellationToken);
            _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            _settingsLoaded = true;
            _logger.LogWarning(ex, "读取实时任务设置失败，已使用默认设置。");
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
                _logger.LogWarning("{Message}", missingWindowMessage);
                return RuntimeTaskStartResult.Failed(missingWindowMessage);
            }

            var shouldBringGameWindowToForeground =
                ShouldBringGameWindowToForegroundOnStart(autoBattleSettings);
            var broughtGameWindowToForeground = false;
            if (shouldBringGameWindowToForeground)
            {
                broughtGameWindowToForeground = _gameWindowService.TryBringGameWindowToForeground(targetWindow);
                if (broughtGameWindowToForeground)
                {
                    _logger.LogInformation(
                        "自动战斗启动：已将游戏窗口切换到前台。InputMethod={InputMethod}, Window={Window}",
                        autoBattleSettings.KeyboardInputMethod,
                        targetWindow.DisplayName);
                }
                else
                {
                    _logger.LogWarning(
                        "自动战斗启动：尝试将游戏窗口切换到前台失败，实时任务继续启动。InputMethod={InputMethod}, Window={Window}",
                        autoBattleSettings.KeyboardInputMethod,
                        targetWindow.DisplayName);
                }
            }

            preparingWindow = targetWindow;
            using var firstFrame = await CaptureFrameAsync(targetWindow, options.CaptureMethod, cancellationToken);
            if (firstFrame is null)
            {
                _screenCaptureService.Release(targetWindow, options.CaptureMethod);
                var captureFailedMessage = $"找到窗口，但未能获取画面：{targetWindow.DisplayName}";
                _logger.LogWarning("{Message}", captureFailedMessage);
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
                _logger.LogWarning("{Message}", unsupportedResolutionMessage);
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
                _logger.LogWarning("{Message}", missingConfigMessage);
                return RuntimeTaskStartResult.Failed(missingConfigMessage);
            }

            var state = new RuntimeTaskState(
                targetWindow,
                recognitionRegionConfig,
                options,
                DateTimeOffset.Now);
            var session = new RuntimeSession(state, _screenCaptureService);
            _session = session;
            preparingWindow = null;
            _isBattleStateActive = false;
            _unrecognizedStateDetectedAt = null;
            _isSuspended = false;
            _suspendedReason = null;
            _debugLog.Reset();
            ResetAutoBattleBattleState();
            ResetEncounterRecordSuppression();
            _encounterStatisticsEnabled = options.EncounterStatisticsEnabled;
            _autoBattleSettings = autoBattleSettings;
            _recognitionOverlayService.Show(state);
            _infoOverlayService.Show(state);
            UpdateInfoOverlayTaskIndicators();
            session.Start(CaptureLoopAsync, RuntimeOcrLoopAsync);

            _logger.LogInformation("实时任务：已启动（窗口 {Window}）", targetWindow.DisplayName);
            _logger.LogDebug(
                "实时任务启动详情：Window={Window}, Client={ClientWidth}x{ClientHeight}, FirstFrame={FrameWidth}x{FrameHeight}, CaptureMethod={CaptureMethod}, OCR={TextRecognitionMethod}, ImageMatching={ImageMatchAlgorithm}, ConfigPath={ConfigPath}",
                targetWindow.DisplayName,
                configResolutionWidth,
                configResolutionHeight,
                firstFrame.Width,
                firstFrame.Height,
                options.CaptureMethod,
                options.TextRecognitionMethod,
                _imageMatchingService.DefaultAlgorithm,
                recognitionRegionConfig.SourcePath);

            _logger.LogDebug(
                "识别区域配置状态：Loaded={Loaded}, EnabledRegions={EnabledRegionCount}, Resolution={ResolutionWidth}x{ResolutionHeight}",
                recognitionRegionConfig.LoadedFromFile,
                recognitionRegionConfig.Regions.Count(region => region.Enabled),
                configResolutionWidth,
                configResolutionHeight);

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
            _logger.LogError(ex, "启动运行任务失败");
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
            ResetAutoBattleBattleState();
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

                if (wasSuspended)
                {
                    wasSuspended = false;
                    ExitCaptureLoopSuspension(state);
                }

                var frameStart = Stopwatch.GetTimestamp();
                CapturedFrame? frame = null;

                try
                {
                    frame = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "捕获画面失败");
                    await DelayAsync(500, cancellationToken);
                }

                try
                {
                    if (frame is not null)
                    {
                        session.PublishFrame(frame, _battle.BattleId);

                        var now = DateTimeOffset.Now;
                        if (now >= nextGameStateScanAt)
                        {
                            var scanSettings = Volatile.Read(ref _runtimeRecognitionSettings);
                            nextGameStateScanAt = now + TimeSpan.FromMilliseconds(scanSettings.GameStateScanIntervalMs);

                            var gameStateScanResult = GameStateScanResult.UnrecognizedPending;
                            try
                            {
                                gameStateScanResult = await ProcessFrameAsync(state, frame, cancellationToken);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex, "状态图像匹配失败");
                            }

                            if (gameStateScanResult == GameStateScanResult.NonBattle)
                            {
                                ResetEncounterRecordSuppression();
                            }
                        }
                    }
                }
                finally
                {
                    frame?.Dispose();
                }

                var elapsedMilliseconds = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                var captureSettings = Volatile.Read(ref _runtimeRecognitionSettings);
                var delay = Math.Max(1, captureSettings.FrameCaptureIntervalMs - (int)elapsedMilliseconds);
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
        _isBattleStateActive = false;
        _unrecognizedStateDetectedAt = null;
        CompleteAutoBattleSkillSelectionState();
        ResetAutoBattleBattleState();
        ResetEncounterRecordSuppression();
        session.ClearFrame();
        _recognitionOverlayService.Hide();
        _logger.LogDebug("实时任务：截图循环进入挂起状态。");
    }

    private void ExitCaptureLoopSuspension(RuntimeTaskState state)
    {
        if (state.Options.RecognitionOverlayEnabled)
        {
            _recognitionOverlayService.Show(state);
        }

        _logger.LogDebug("实时任务：截图循环退出挂起状态，重新开始识别。");
    }

    private async Task RuntimeOcrLoopAsync(RuntimeSession session, CancellationToken cancellationToken)
    {
        var state = session.State;
        Task? activeScanTask = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref _runtimeRecognitionSettings);
                await Task.Delay(settings.OcrScanIntervalMs, cancellationToken);

                if (activeScanTask is not null)
                {
                    if (activeScanTask.IsCompleted)
                    {
                        try
                        {
                            await activeScanTask;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Runtime OCR scan failed.");
                        }

                        activeScanTask = null;
                    }
                    else
                    {
                        _debugLog.Write(
                            CreateDebugLogKey("runtime-ocr-skip-busy"),
                            "busy",
                            "后台 OCR 本轮跳过：上一次 OCR 仍在执行。");
                        continue;
                    }
                }

                if (_isSuspended || !_isBattleStateActive)
                {
                    continue;
                }

                var battleId = _battle.BattleId;
                var frame = session.RentFrame(battleId);
                if (frame is null)
                {
                    continue;
                }

                activeScanTask = RunRuntimeOcrScanAsync(state, frame, battleId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (activeScanTask is not null)
            {
                try
                {
                    await activeScanTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Runtime OCR scan failed.");
                }
            }

            session.ClearFrame();
        }
    }

    private async Task RunRuntimeOcrScanAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        long battleId,
        CancellationToken cancellationToken)
    {
        using (frame)
        {
            try
            {
                await UpdateRuntimeEncounterOcrSignalsAsync(state, frame, battleId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runtime OCR scan failed.");
            }
        }
    }

    private async Task<GameStateScanResult> ProcessFrameAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_isBattleStateActive)
        {
            CompleteAutoBattleSkillSelectionState();
            ResetAutoBattleBattleState();

            if (await _battleScreen.IsBattleChatVisibleAsync(state, frame, cancellationToken))
            {
                _isBattleStateActive = true;
                _debugLog.Reset();
                return await ProcessBattleFrameAsync(
                    state,
                    frame,
                    isBattleChatVisible: true,
                    cancellationToken);
            }

            return await UpdateMagicPointSnapshotAsync(state, frame, cancellationToken);
        }

        if (await TryUpdateMagicPointWorldSnapshotAsync(state, frame, cancellationToken))
        {
            _isBattleStateActive = false;
            CompleteAutoBattleSkillSelectionState();
            ResetAutoBattleBattleState();
            return GameStateScanResult.NonBattle;
        }

        return await ProcessBattleFrameAsync(
            state,
            frame,
            isBattleChatVisible: null,
            cancellationToken);
    }

    private async Task<GameStateScanResult> ProcessBattleFrameAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        bool? isBattleChatVisible,
        CancellationToken cancellationToken)
    {
        await UpdateEncounterCaptureButtonStateAsync(state, frame, cancellationToken);
        var screen = await _battleScreen.RecognizeAsync(state, frame, isBattleChatVisible, cancellationToken);
        if (screen != BattleScreen.PetSwitching) _battle.ObservePetSwitching(false);

        switch (screen)
        {
            case BattleScreen.SkillSelection:
                var recovered = !_battle.IsSuspendedForShiny
                    && await TryHandleAutoBattleSkillReleaseFailureAsync(state, frame, cancellationToken);
                if (!recovered) await HandleAutoBattleSkillSelectionAsync(state, frame, cancellationToken);
                break;
            case BattleScreen.PetSwitching:
                CompleteAutoBattleSkillSelectionState();
                if (!_battle.IsSuspendedForShiny) await HandleAutoBattlePetSwitchingAsync(state, cancellationToken);
                break;
            case BattleScreen.Chat:
                if (!_battle.IsSuspendedForShiny) CompleteAutoBattleSkillSelectionState();
                break;
            case BattleScreen.Transition:
                CompleteAutoBattleSkillSelectionState();
                break;
        }

        var description = screen switch
        {
            _ when screen != BattleScreen.Transition && _battle.IsSuspendedForShiny => "战斗中 - 异色保护",
            BattleScreen.SkillSelection => "战斗中 - 技能选择",
            BattleScreen.PetSwitching => "战斗中 - 切换精灵",
            _ => "战斗中"
        };
        UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(description, DateTimeOffset.Now));
        return GameStateScanResult.Battle;
    }

    private void UpdateRecognizedInfoOverlaySnapshot(InfoOverlaySnapshot snapshot)
    {
        _unrecognizedStateDetectedAt = null;
        _infoOverlayService.UpdateSnapshot(snapshot);
    }

    private GameStateScanResult TryUpdateUnrecognizedInfoOverlaySnapshot(DateTimeOffset now)
    {
        _unrecognizedStateDetectedAt ??= now;
        if (now - _unrecognizedStateDetectedAt.Value < UnrecognizedStateConfirmDelay)
        {
            return GameStateScanResult.UnrecognizedPending;
        }

        _infoOverlayService.UpdateSnapshot(CreateInfoOverlaySnapshot("未识别", now));
        return GameStateScanResult.NonBattle;
    }

    private InfoOverlaySnapshot CreateInfoOverlaySnapshot(
        string statusText,
        DateTimeOffset updatedAt,
        int? magicPointCount = null,
        int magicPointMaximum = MagicPointSlotCount)
    {
        return new InfoOverlaySnapshot(
            statusText,
            GetCurrentSeasonEncounterCounters(),
            updatedAt,
            magicPointCount,
            magicPointMaximum,
            GetCurrentPendingShinyCapture());
    }

    private async Task<bool> TryUpdateMagicPointWorldSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var magicPointRegion = FindRegion(state.RecognitionRegionConfig, MagicPointRegionIds);
        var magicPointTemplatePath = GetResolutionTemplatePath(
            state.RecognitionRegionConfig,
            MagicPointTemplateName);
        if (!_frameRecognizer.TemplateExists(magicPointTemplatePath))
        {
            return false;
        }

        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            magicPointRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var matchOptions = CreateScaledImageMatchOptions(
            MagicPointMatchOptions,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        var matchResult = await _imageMatchingService.FindMatchesAsync(
            frame,
            frameRegion,
            magicPointTemplatePath,
            MagicPointSlotCount,
            matchOptions,
            cancellationToken: cancellationToken);
        var magicPointCount = matchResult.Matches.Count;
        var bestMatchScore = matchResult.BestScore;

        _recognitionOverlayService.ShowImageMatchResult(magicPointRegion.Id, bestMatchScore);
        _debugLog.Write(
            CreateDebugLogKey("game-state-magic-point-active", magicPointRegion.Id),
            $"{magicPointCount}/{MagicPointSlotCount}",
            "状态识别目标结果：Target=大世界魔力点 Region={RegionId}, Count={Count}/{Maximum}, FrameRegion={X},{Y},{Width}x{Height}",
            magicPointRegion.Id,
            magicPointCount,
            MagicPointSlotCount,
            frameRegion.X,
            frameRegion.Y,
            frameRegion.Width,
            frameRegion.Height);

        if (magicPointCount <= 0)
        {
            return false;
        }

        UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
            "大世界",
            DateTimeOffset.Now,
            magicPointCount,
            MagicPointSlotCount));
        return true;
    }

    private async Task<GameStateScanResult> UpdateMagicPointSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var magicPointRegion = FindRegion(state.RecognitionRegionConfig, MagicPointRegionIds);
        var magicPointTemplatePath = GetResolutionTemplatePath(
            state.RecognitionRegionConfig,
            MagicPointTemplateName);
        if (!_frameRecognizer.TemplateExists(magicPointTemplatePath))
        {
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                $"未找到 {magicPointTemplatePath}",
                DateTimeOffset.Now));
            return GameStateScanResult.NonBattle;
        }

        var frameRegion = RecognitionRegionImageHelper.ToFrameRegion(
            magicPointRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                "魔力区域不在截图内",
                DateTimeOffset.Now));
            return GameStateScanResult.NonBattle;
        }

        var matchOptions = CreateScaledImageMatchOptions(
            MagicPointMatchOptions,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        var matchResult = await _imageMatchingService.FindMatchesAsync(
            frame,
            frameRegion,
            magicPointTemplatePath,
            MagicPointSlotCount,
            matchOptions,
            cancellationToken: cancellationToken);
        var magicPointCount = matchResult.Matches.Count;
        var bestMatchScore = matchResult.BestScore;

        _recognitionOverlayService.ShowImageMatchResult(magicPointRegion.Id, bestMatchScore);
        _debugLog.Write(
            CreateDebugLogKey("game-state-magic-point-world", magicPointRegion.Id),
            $"{magicPointCount}/{MagicPointSlotCount}",
            "状态识别目标结果：Target=魔力点, Region={RegionId}, Count={Count}/{Maximum}, FrameRegion={X},{Y},{Width}x{Height}",
            magicPointRegion.Id,
            magicPointCount,
            MagicPointSlotCount,
            frameRegion.X,
            frameRegion.Y,
            frameRegion.Width,
            frameRegion.Height);

        var now = DateTimeOffset.Now;
        if (magicPointCount <= 0)
        {
            return TryUpdateUnrecognizedInfoOverlaySnapshot(now);
        }

        UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
            "大世界",
            now,
            magicPointCount,
            MagicPointSlotCount));
        return GameStateScanResult.NonBattle;
    }

    public void SetRuntimeRecognitionSettings(RuntimeRecognitionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = NormalizeRuntimeRecognitionSettings(settings);
        Volatile.Write(ref _runtimeRecognitionSettings, normalized);
        _ = SaveRuntimeRecognitionSettingsAsync(normalized);
        NotifySettingsChanged();
    }

    private async Task SaveRuntimeRecognitionSettingsAsync(RuntimeRecognitionSettings settings)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.RuntimeRecognitionSettings, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存运行频率设置失败。");
        }
    }

    private static RuntimeRecognitionSettings NormalizeRuntimeRecognitionSettings(RuntimeRecognitionSettings? settings)
    {
        var source = settings ?? RuntimeRecognitionSettings.CreateDefault();
        return new RuntimeRecognitionSettings
        {
            FrameCaptureIntervalMs = Math.Clamp(
                source.FrameCaptureIntervalMs,
                RuntimeRecognitionSettings.MinimumFrameCaptureIntervalMs,
                RuntimeRecognitionSettings.MaximumIntervalMs),
            GameStateScanIntervalMs = Math.Clamp(
                source.GameStateScanIntervalMs,
                RuntimeRecognitionSettings.MinimumGameStateScanIntervalMs,
                RuntimeRecognitionSettings.MaximumIntervalMs),
            OcrScanIntervalMs = Math.Clamp(
                source.OcrScanIntervalMs,
                RuntimeRecognitionSettings.MinimumOcrScanIntervalMs,
                RuntimeRecognitionSettings.MaximumIntervalMs)
        };
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

    private enum GameStateScanResult
    {
        Battle,
        NonBattle,
        UnrecognizedPending
    }

}
