using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Input;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService : IRuntimeTaskService
{
    private const int TargetFrameIntervalMs = 33;
    private const int MagicPointSlotCount = 6;
    private const string MagicPointTemplateName = "magic-point.png";
    private static readonly TimeSpan GameStateScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RuntimeOcrScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnrecognizedStateConfirmDelay = TimeSpan.FromSeconds(2);
    private static readonly string[] MagicPointRegionIds =
    [
        "magic-point"
    ];
    private static readonly ImageMatchOptions MagicPointMatchOptions = new()
    {
        MinimumScore = 0.92,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    private readonly IGameWindowService _gameWindowService;
    private readonly IKeyboardInputService _keyboardInputService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly IImageMatchingService _imageMatchingService;
    private readonly ITextRecognitionService _textRecognitionService;
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
    private readonly object _latestRuntimeOcrFrameLock = new();

    private CancellationTokenSource? _captureCancellationTokenSource;
    private Task? _captureTask;
    private Task? _runtimeOcrTask;
    private CapturedFrame? _latestRuntimeOcrFrame;
    private bool _settingsLoaded;
    private bool _isBattleStateActive;
    private DateTimeOffset? _unrecognizedStateDetectedAt;

    public event EventHandler? SettingsChanged;

    public RuntimeTaskState? CurrentState
    {
        get;
        private set;
    }

    public bool IsRunning => CurrentState is not null;

    public RuntimeTaskService(
        IGameWindowService gameWindowService,
        IKeyboardInputService keyboardInputService,
        IScreenCaptureService screenCaptureService,
        IRecognitionRegionConfigService recognitionRegionConfigService,
        IImageMatchingService imageMatchingService,
        ITextRecognitionService textRecognitionService,
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
        _textRecognitionService = textRecognitionService;
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
            _autoBattleSettings = NormalizeAutoBattleSettings(savedAutoBattleSettings);
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
        try
        {
            if (CurrentState is not null)
            {
                return RuntimeTaskStartResult.Started(CurrentState);
            }

            var autoBattleSettings = NormalizeAutoBattleSettings(options.AutoBattleSettings);
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
            var recognitionRegionConfig = _recognitionRegionConfigService.LoadForResolution(
                configResolutionWidth,
                configResolutionHeight);
            var state = new RuntimeTaskState(
                targetWindow,
                recognitionRegionConfig,
                options,
                DateTimeOffset.Now);
            var cancellationTokenSource = new CancellationTokenSource();
            _captureCancellationTokenSource = cancellationTokenSource;
            CurrentState = state;
            _isBattleStateActive = false;
            _unrecognizedStateDetectedAt = null;
            ResetDeduplicatedDebugLogs();
            ResetAutoBattleBattleState();
            ResetEncounterRecordSuppression();
            _encounterStatisticsEnabled = options.EncounterStatisticsEnabled;
            _autoBattleSettings = autoBattleSettings;
            _recognitionOverlayService.Show(state);
            _infoOverlayService.Show(state);
            UpdateInfoOverlayTaskIndicators();
            _captureTask = Task.Run(
                () => CaptureLoopAsync(state, cancellationTokenSource.Token),
                cancellationTokenSource.Token);
            _runtimeOcrTask = Task.Run(
                () => RuntimeOcrLoopAsync(state, cancellationTokenSource.Token),
                cancellationTokenSource.Token);

            _logger.LogInformation("实时任务：已启动（窗口 {Window}）", targetWindow.DisplayName);
            _logger.LogDebug(
                "实时任务启动详情：Window={Window}, Client={ClientWidth}x{ClientHeight}, FirstFrame={FrameWidth}x{FrameHeight}, CaptureMethod={CaptureMethod}, OCR={TextRecognitionMethod}, ConfigPath={ConfigPath}",
                targetWindow.DisplayName,
                configResolutionWidth,
                configResolutionHeight,
                firstFrame.Width,
                firstFrame.Height,
                options.CaptureMethod,
                options.TextRecognitionMethod,
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
            return RuntimeTaskStartResult.Failed("启动任务已取消。");
        }
        catch (Exception ex)
        {
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
        CancellationTokenSource? cancellationTokenSource = null;
        Task? captureTask = null;
        Task? runtimeOcrTask = null;
        RuntimeTaskState? state = null;

        await _lifecycleLock.WaitAsync();
        try
        {
            if (CurrentState is null)
            {
                return;
            }

            cancellationTokenSource = _captureCancellationTokenSource;
            captureTask = _captureTask;
            runtimeOcrTask = _runtimeOcrTask;
            state = CurrentState;
            _captureCancellationTokenSource = null;
            _captureTask = null;
            _runtimeOcrTask = null;
            CurrentState = null;
            cancellationTokenSource?.Cancel();
            _recognitionOverlayService.Hide();
            _infoOverlayService.Hide();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        try
        {
            var runningTasks = new[] { captureTask, runtimeOcrTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (runningTasks.Length > 0)
            {
                await Task.WhenAll(runningTasks);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ClearLatestRuntimeOcrFrame();
            if (state is not null)
            {
                _screenCaptureService.Release(state.TargetWindow, state.Options.CaptureMethod);
            }

            cancellationTokenSource?.Dispose();
            _logger.LogInformation("实时任务：已停止");
        }
    }

    private async Task CaptureLoopAsync(RuntimeTaskState state, CancellationToken cancellationToken)
    {
        var nextGameStateScanAt = DateTimeOffset.MinValue;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
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
                    if (frame is not null
                        && (state.Options.InfoOverlayEnabled
                            || EncounterStatisticsEnabled
                            || _autoBattleSettings.IsEnabled))
                    {
                        PublishLatestRuntimeOcrFrame(frame);

                        var now = DateTimeOffset.Now;
                        if (now >= nextGameStateScanAt)
                        {
                            nextGameStateScanAt = now + GameStateScanInterval;

                            var gameStateScanResult = GameStateScanResult.UnrecognizedPending;
                            try
                            {
                                gameStateScanResult = await UpdateGameStateSnapshotAsync(state, frame, cancellationToken);
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
                var delay = Math.Max(1, TargetFrameIntervalMs - (int)elapsedMilliseconds);
                await DelayAsync(delay, cancellationToken);
            }
        }
        finally
        {
            _screenCaptureService.Release(state.TargetWindow, state.Options.CaptureMethod);
        }
    }

    private async Task RuntimeOcrLoopAsync(RuntimeTaskState state, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var scanStart = Stopwatch.GetTimestamp();

                try
                {
                    using var frame = RentLatestRuntimeOcrFrame();
                    if (frame is not null && _isBattleStateActive)
                    {
                        await UpdateRuntimeOcrSignalsAsync(state, frame, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Runtime OCR scan failed.");
                }

                var elapsedMilliseconds = Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds;
                var delay = Math.Max(1, RuntimeOcrScanInterval.TotalMilliseconds - elapsedMilliseconds);
                await DelayAsync((int)delay, cancellationToken);
            }
        }
        finally
        {
            ClearLatestRuntimeOcrFrame();
        }
    }

    private async Task UpdateRuntimeOcrSignalsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (EncounterStatisticsEnabled)
        {
            await TryUpdateEncounterStatisticsAsync(state, frame, cancellationToken);
            return;
        }

        var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
        if (!settings.IsEnabled)
        {
            return;
        }

        var isAutoBattleSuspendedForShiny =
            await TrySuspendAutoBattleForShinyAsync(state, frame, cancellationToken);
        if (!isAutoBattleSuspendedForShiny)
        {
            await TryUpdateAutoBattleEncounterRelievedActionModeAsync(state, frame, cancellationToken);
        }
    }

    private void PublishLatestRuntimeOcrFrame(CapturedFrame frame)
    {
        CapturedFrame frameReference;
        try
        {
            frameReference = frame.AddReference();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        CapturedFrame? previousFrame;
        lock (_latestRuntimeOcrFrameLock)
        {
            previousFrame = _latestRuntimeOcrFrame;
            _latestRuntimeOcrFrame = frameReference;
        }

        previousFrame?.Dispose();
    }

    private CapturedFrame? RentLatestRuntimeOcrFrame()
    {
        lock (_latestRuntimeOcrFrameLock)
        {
            try
            {
                return _latestRuntimeOcrFrame?.AddReference();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    private void ClearLatestRuntimeOcrFrame()
    {
        CapturedFrame? frame;
        lock (_latestRuntimeOcrFrameLock)
        {
            frame = _latestRuntimeOcrFrame;
            _latestRuntimeOcrFrame = null;
        }

        frame?.Dispose();
    }

    private async Task<GameStateScanResult> UpdateGameStateSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_isBattleStateActive)
        {
            CompleteAutoBattleSkillSelectionState();
            ResetAutoBattleBattleState();

            if (await IsBattleChatVisibleAsync(state, frame, cancellationToken))
            {
                _isBattleStateActive = true;
                ResetDeduplicatedDebugLogs();
                return await UpdateActiveBattleSnapshotAsync(
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

        return await UpdateActiveBattleSnapshotAsync(
            state,
            frame,
            isBattleChatVisible: null,
            cancellationToken);
    }

    private async Task<GameStateScanResult> UpdateActiveBattleSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        bool? isBattleChatVisible,
        CancellationToken cancellationToken)
    {
        var isSkillSelectionVisible = await IsBattleSkillSelectionVisibleAsync(state, frame, cancellationToken);
        if (isSkillSelectionVisible)
        {
            _wasAutoBattlePetSwitchingVisible = false;
            var isAutoBattleSuspendedForShiny = _isAutoBattleSuspendedForShiny;
            var handledSkillFailure = false;
            if (!isAutoBattleSuspendedForShiny)
            {
                handledSkillFailure = await TryHandleAutoBattleSkillReleaseFailureAsync(
                    state,
                    frame,
                    cancellationToken);
            }

            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                isAutoBattleSuspendedForShiny ? "战斗中 - 异色保护" : "战斗中 - 技能选择",
                DateTimeOffset.Now));
            if (!isAutoBattleSuspendedForShiny && !handledSkillFailure)
            {
                await HandleAutoBattleSkillSelectionAsync(state, cancellationToken);
            }

            return GameStateScanResult.Battle;
        }

        var isPetSwitchingVisible = await IsBattlePetSwitchingAsync(state, frame, cancellationToken);
        if (isPetSwitchingVisible)
        {
            var isAutoBattleSuspendedForShiny = _isAutoBattleSuspendedForShiny;
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                isAutoBattleSuspendedForShiny ? "战斗中 - 异色保护" : "战斗中 - 切换精灵",
                DateTimeOffset.Now));
            CompleteAutoBattleSkillSelectionState();
            if (!isAutoBattleSuspendedForShiny)
            {
                await HandleAutoBattlePetSwitchingAsync(state, cancellationToken);
            }

            return GameStateScanResult.Battle;
        }

        _wasAutoBattlePetSwitchingVisible = false;

        var chatVisible = isBattleChatVisible ?? await IsBattleChatVisibleAsync(state, frame, cancellationToken);
        if (chatVisible)
        {
            var isAutoBattleSuspendedForShiny = _isAutoBattleSuspendedForShiny;
            if (!isAutoBattleSuspendedForShiny)
            {
                CompleteAutoBattleSkillSelectionState();
            }

            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                isAutoBattleSuspendedForShiny ? "战斗中 - 异色保护" : "战斗中",
                DateTimeOffset.Now));
            return GameStateScanResult.Battle;
        }

        CompleteAutoBattleSkillSelectionState();
        UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
            "战斗中",
            DateTimeOffset.Now));
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
        if (!TemplateExists(MagicPointTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
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
        var magicPointCount = 0;
        foreach (var slotRegion in SplitMagicPointSlots(frameRegion))
        {
            var result = await _imageMatchingService.MatchAsync(
                frame,
                slotRegion,
                MagicPointTemplateName,
                matchOptions,
                cancellationToken);
            if (result.IsMatch)
            {
                magicPointCount++;
            }
        }

        LogDebugOncePerValue(
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
        if (!TemplateExists(MagicPointTemplateName))
        {
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                "未找到 magic-point.png",
                DateTimeOffset.Now));
            return GameStateScanResult.NonBattle;
        }

        var frameRegion = ToFrameRegion(
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
        var magicPointCount = 0;
        foreach (var slotRegion in SplitMagicPointSlots(frameRegion))
        {
            var result = await _imageMatchingService.MatchAsync(
                frame,
                slotRegion,
                MagicPointTemplateName,
                matchOptions,
                cancellationToken);
            if (result.IsMatch)
            {
                magicPointCount++;
            }
        }

        LogDebugOncePerValue(
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

    private async Task<string> RecognizeRegionTextAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        CancellationToken cancellationToken,
        string taskName)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        var frameRegion = ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("ocr-skip-outside-frame", taskName, region.Id),
                $"{frameRegion.X},{frameRegion.Y},{frameRegion.Width}x{frameRegion.Height}",
                "{TaskName} OCR跳过：识别区域不在截图内。Region={RegionId}, Aliases={RegionAliases}",
                taskName,
                region.Id,
                string.Join("|", regionAliases));
            return string.Empty;
        }

        var recognitionMethod = _textRecognitionService
            .GetMethods()
            .FirstOrDefault(method => method.Method == state.Options.TextRecognitionMethod && method.IsAvailable);
        if (recognitionMethod is null)
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("ocr-skip-method-unavailable", taskName, region.Id, state.Options.TextRecognitionMethod),
                "method-unavailable",
                "{TaskName} OCR跳过：OCR 方法不可用。Method={TextRecognitionMethod}, Region={RegionId}",
                taskName,
                state.Options.TextRecognitionMethod,
                region.Id);
            return string.Empty;
        }

        var imageBytes = await EncodeFrameRegionPngAsync(frame, frameRegion, cancellationToken);
        var result = await _textRecognitionService.RecognizeAsync(
            imageBytes,
            recognitionMethod.Method,
            cancellationToken);
        LogDebugOncePerValue(
            CreateDebugLogKey("ocr-result", taskName, region.Id, recognitionMethod.Method),
            CreateTextDebugFingerprint(result.Text),
            "{TaskName} OCR结果：Region={RegionId}, Method={TextRecognitionMethod}, FrameRegion={X},{Y},{Width}x{Height}, Text={Text}",
            taskName,
            region.Id,
            recognitionMethod.Method,
            frameRegion.X,
            frameRegion.Y,
            frameRegion.Width,
            frameRegion.Height,
            FormatLogText(result.Text));
        return result.Text;
    }

    private async Task<bool> MatchRuntimeTemplateAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        string templateName,
        ImageMatchOptions options,
        string taskName,
        string targetName,
        CancellationToken cancellationToken)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        if (!TemplateExists(templateName))
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("template-skip-missing-template", taskName, targetName, region.Id, templateName),
                "missing-template",
                "{TaskName} 目标识别跳过：未找到模板。Target={Target}, Region={RegionId}, Template={Template}",
                taskName,
                targetName,
                region.Id,
                templateName);
            return false;
        }

        var frameRegion = ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("template-skip-outside-frame", taskName, targetName, region.Id, templateName),
                $"{frameRegion.X},{frameRegion.Y},{frameRegion.Width}x{frameRegion.Height}",
                "{TaskName} 目标识别跳过：识别区域不在截图内。Target={Target}, Region={RegionId}, Template={Template}",
                taskName,
                targetName,
                region.Id,
                templateName);
            return false;
        }

        var matchOptions = CreateScaledImageMatchOptions(
            options,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            templateName,
            matchOptions,
            cancellationToken);
        LogDebugOncePerValue(
            CreateDebugLogKey("template-result", taskName, targetName, region.Id, templateName),
            CreateBooleanDebugFingerprint(result.IsMatch),
            "{TaskName} 目标识别结果：Target={Target}, Region={RegionId}, Template={Template}, Score={Score:F3}, Threshold={Threshold:F3}, IsMatch={IsMatch}, FrameRegion={X},{Y},{Width}x{Height}",
            taskName,
            targetName,
            region.Id,
            templateName,
            result.Score,
            matchOptions.MinimumScore,
            result.IsMatch,
            frameRegion.X,
            frameRegion.Y,
            frameRegion.Width,
            frameRegion.Height);
        return result.IsMatch;
    }

    private static string FormatLogText(string? text, int maximumLength = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalized = string.Join(
            " ",
            text
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength]}...";
    }

    private static async Task<byte[]> EncodeFrameRegionPngAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        CancellationToken cancellationToken)
    {
        var pixels = CropFrame(frame, region);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)region.Width,
            (uint)region.Height,
            96,
            96,
            pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);

        stream.Seek(0);
        var encodedLength = checked((int)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var loadedLength = await reader.LoadAsync((uint)encodedLength).AsTask(cancellationToken);
        if (loadedLength != (uint)encodedLength)
        {
            throw new InvalidDataException("区域截图编码结果读取不完整。");
        }

        var encodedBytes = new byte[encodedLength];
        reader.ReadBytes(encodedBytes);
        return encodedBytes;
    }

    private static byte[] CropFrame(CapturedFrame frame, RecognitionRegion region)
    {
        var expectedLength = checked(frame.Width * frame.Height * 4);
        if (frame.PixelByteLength < expectedLength)
        {
            throw new InvalidDataException("捕获帧像素数据不完整。");
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

    private bool TemplateExists(string templateName)
    {
        return File.Exists(Path.Combine(_imageMatchingService.TemplateDirectory, templateName));
    }

    private static RecognitionRegion FindRegion(
        RecognitionRegionConfig config,
        IReadOnlyList<string> aliases)
    {
        return config.Regions.FirstOrDefault(region => IsRegionMatch(region, aliases))
            ?? throw new InvalidOperationException(
                $"识别区域配置缺少启用区域：{string.Join(", ", aliases)}。配置文件：{config.SourcePath}");
    }

    private static bool IsRegionMatch(RecognitionRegion region, IReadOnlyList<string> aliases)
    {
        if (!region.Enabled || string.IsNullOrWhiteSpace(region.Id))
        {
            return false;
        }

        var id = region.Id.Trim();
        return aliases.Any(alias => string.Equals(id, alias, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<RecognitionRegion> SplitMagicPointSlots(RecognitionRegion region)
    {
        for (var index = 0; index < MagicPointSlotCount; index++)
        {
            var left = (int)Math.Round(region.Width * (double)index / MagicPointSlotCount);
            var right = (int)Math.Round(region.Width * (double)(index + 1) / MagicPointSlotCount);
            yield return new RecognitionRegion
            {
                Id = $"{region.Id}-{index + 1}",
                X = region.X + left,
                Y = region.Y,
                Width = Math.Max(1, right - left),
                Height = region.Height,
                Enabled = region.Enabled
            };
        }
    }

    private static ImageMatchOptions CreateScaledImageMatchOptions(
        ImageMatchOptions options,
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        RecognitionRegionConfig config)
    {
        var configWidth = config.ResolutionWidth > 0
            ? config.ResolutionWidth
            : targetWindow.HasClientArea ? targetWindow.ClientWidth : frame.Width;
        var configHeight = config.ResolutionHeight > 0
            ? config.ResolutionHeight
            : targetWindow.HasClientArea ? targetWindow.ClientHeight : frame.Height;

        if (configWidth <= 0 || configHeight <= 0)
        {
            return CloneImageMatchOptions(options, 1, 1);
        }

        _ = TryGetClientAreaInCapturedFrame(
            frame,
            targetWindow,
            out _,
            out _,
            out var sourceWidth,
            out var sourceHeight);

        var scaleX = sourceWidth > 0 ? sourceWidth / (double)configWidth : 1;
        var scaleY = sourceHeight > 0 ? sourceHeight / (double)configHeight : 1;
        return CloneImageMatchOptions(options, scaleX, scaleY);
    }

    private static ImageMatchOptions CloneImageMatchOptions(ImageMatchOptions options, double scaleX, double scaleY)
    {
        return new ImageMatchOptions
        {
            MinimumScore = options.MinimumScore,
            AlphaThreshold = options.AlphaThreshold,
            SearchStep = options.SearchStep,
            TemplateScaleX = options.TemplateScaleX * scaleX,
            TemplateScaleY = options.TemplateScaleY * scaleY
        };
    }

    private static RecognitionRegion ToFrameRegion(
        RecognitionRegion region,
        CapturedFrame frame,
        CaptureTargetWindow targetWindow,
        RecognitionRegionConfig config)
    {
        var configWidth = config.ResolutionWidth > 0
            ? config.ResolutionWidth
            : targetWindow.HasClientArea ? targetWindow.ClientWidth : frame.Width;
        var configHeight = config.ResolutionHeight > 0
            ? config.ResolutionHeight
            : targetWindow.HasClientArea ? targetWindow.ClientHeight : frame.Height;

        if (configWidth <= 0 || configHeight <= 0)
        {
            return new RecognitionRegion();
        }

        _ = TryGetClientAreaInCapturedFrame(
            frame,
            targetWindow,
            out var clientX,
            out var clientY,
            out var sourceWidth,
            out var sourceHeight);

        var sourceRegion = new RecognitionRegion
        {
            Id = region.Id,
            X = ScaleValue(region.X, configWidth, sourceWidth),
            Y = ScaleValue(region.Y, configHeight, sourceHeight),
            Width = Math.Max(1, ScaleValue(region.Width, configWidth, sourceWidth)),
            Height = Math.Max(1, ScaleValue(region.Height, configHeight, sourceHeight)),
            Enabled = region.Enabled
        };

        var x = clientX + sourceRegion.X;
        var y = clientY + sourceRegion.Y;
        var right = clientX + sourceRegion.X + sourceRegion.Width;
        var bottom = clientY + sourceRegion.Y + sourceRegion.Height;
        var clampedX = ClampToRange(x, 0, frame.Width);
        var clampedY = ClampToRange(y, 0, frame.Height);
        var clampedRight = ClampToRange(right, 0, frame.Width);
        var clampedBottom = ClampToRange(bottom, 0, frame.Height);

        return clampedRight <= clampedX || clampedBottom <= clampedY
            ? new RecognitionRegion()
            : new RecognitionRegion
            {
                Id = region.Id,
                X = clampedX,
                Y = clampedY,
                Width = clampedRight - clampedX,
                Height = clampedBottom - clampedY,
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

    private static int ScaleValue(int value, int sourceSize, int targetSize)
    {
        if (sourceSize <= 0 || targetSize <= 0)
        {
            return value;
        }

        return (int)Math.Round(value * (double)targetSize / sourceSize);
    }

    private static int ClampToRange(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
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
