using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Recognition;
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

public sealed class RuntimeTaskService : IRuntimeTaskService
{
    private const int TargetFrameIntervalMs = 33;
    private const int MagicPointSlotCount = 6;
    private const string MagicPointTemplateName = "magic-point.png";
    private const string BattleChatTemplateName = "battle-chat.png";
    private const string BattleSkillTemplateName = "battle-button-skill.png";
    private const string BattleSpaceTemplateName = "battle-space.png";
    private const string AutoBattleSkillPlaceholder = "{skill}";

    private static readonly TimeSpan GameStateScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EncounterScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EncounterDuplicateSuppressWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan UnrecognizedStateConfirmDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AutoBattleSkillSelectionActionDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoBattleSkillSelectionRetryDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AutoBattlePetSwitchProbeDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] AutoBattleDefaultRoundOrder =
    [
        "1",
        "2",
        "3",
        "4",
        "X"
    ];
    private static readonly string[] MagicPointRegionIds =
    [
        "magic-point"
    ];
    private static readonly string[] BattleChatRegionIds =
    [
        "battle-button-chat"
    ];
    private static readonly string[] BattleSkillRegionIds =
    [
        "battle-button-skill"
    ];
    private static readonly string[] BattleMagicRegionIds =
    [
        "battle-magic"
    ];
    private static readonly string[] BattleTipRelieveRegionIds =
    [
        "battle-tip-relieve"
    ];
    private static readonly string[] BattleTipEnergyRegionIds =
    [
        "battle-tip-energe"
    ];
    private static readonly string[] BattleEnemyNameRegionIds =
    [
        "battle-enemy-name"
    ];
    private static readonly string[] BattleSpaceRegionIds =
    [
        "battle-space"
    ];
    private static readonly ImageMatchOptions MagicPointMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleChatMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleSkillMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleSpaceMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly KeyboardInputOptions AutoBattleKeyboardInputOptions = new()
    {
        HoldDurationMs = 45,
        IntervalMs = 120
    };

    private readonly IGameWindowService _gameWindowService;
    private readonly IKeyboardInputService _keyboardInputService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IRecognitionRegionConfigService _recognitionRegionConfigService;
    private readonly IImageMatchingService _imageMatchingService;
    private readonly ITextRecognitionService _textRecognitionService;
    private readonly IEncounterSeasonConfigService _encounterSeasonConfigService;
    private readonly IStatisticsService _statisticsService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IRecognitionOverlayService _recognitionOverlayService;
    private readonly IInfoOverlayService _infoOverlayService;
    private readonly ILogger<RuntimeTaskService> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly SemaphoreSlim _autoBattleActionLock = new(1, 1);
    private readonly object _encounterRecordLock = new();

    private CancellationTokenSource? _captureCancellationTokenSource;
    private Task? _captureTask;
    private volatile bool _encounterStatisticsEnabled = true;
    private AutoBattleSettings _autoBattleSettings = AutoBattleSettings.CreateDefault();
    private bool _settingsLoaded;
    private bool _hasActiveEncounterRecord;
    private int _autoBattleRoundIndex;
    private bool _wasAutoBattleSkillSelectionVisible;
    private bool _wasAutoBattlePetSwitchingVisible;
    private bool _pendingAutoBattleRoundAdvance;
    private DateTimeOffset? _autoBattleSkillSelectionVisibleSince;
    private DateTimeOffset? _lastAutoBattleSkillSelectionActionAt;
    private DateTimeOffset? _unrecognizedStateDetectedAt;
    private string? _lastRecordedEncounterSeasonId;
    private string? _lastRecordedEncounterName;
    private DateTimeOffset _lastRecordedEncounterAt;

    public RuntimeTaskState? CurrentState
    {
        get;
        private set;
    }

    public bool IsRunning => CurrentState is not null;

    public bool EncounterStatisticsEnabled => _encounterStatisticsEnabled;

    public AutoBattleSettings AutoBattleSettings => _autoBattleSettings.Clone();

    public RuntimeTaskService(
        IGameWindowService gameWindowService,
        IKeyboardInputService keyboardInputService,
        IScreenCaptureService screenCaptureService,
        IRecognitionRegionConfigService recognitionRegionConfigService,
        IImageMatchingService imageMatchingService,
        ITextRecognitionService textRecognitionService,
        IEncounterSeasonConfigService encounterSeasonConfigService,
        IStatisticsService statisticsService,
        ILocalSettingsService localSettingsService,
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
        _statisticsService = statisticsService;
        _localSettingsService = localSettingsService;
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

            var targetWindow = _gameWindowService.FindGameWindow();
            if (targetWindow is null)
            {
                var missingWindowMessage = $"未找到目标游戏窗口：{_gameWindowService.TargetProcessName}";
                _logger.LogWarning("{Message}", missingWindowMessage);
                return RuntimeTaskStartResult.Failed(missingWindowMessage);
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
            _encounterStatisticsEnabled = options.EncounterStatisticsEnabled;
            _autoBattleSettings = NormalizeAutoBattleSettings(options.AutoBattleSettings);
            _recognitionOverlayService.Show(state);
            _infoOverlayService.Show(state);
            _captureTask = Task.Run(
                () => CaptureLoopAsync(state, cancellationTokenSource.Token),
                cancellationTokenSource.Token);

            _logger.LogInformation(
                "运行任务已启动，窗口: {Window}, 客户区: {ClientWidth}x{ClientHeight}, 首帧: {FrameWidth}x{FrameHeight}, 截图方式: {CaptureMethod}, OCR: {TextRecognitionMethod}, 区域配置: {ConfigPath}",
                targetWindow.DisplayName,
                configResolutionWidth,
                configResolutionHeight,
                firstFrame.Width,
                firstFrame.Height,
                options.CaptureMethod,
                options.TextRecognitionMethod,
                recognitionRegionConfig.SourcePath);

            _logger.LogInformation(
                "识别区域配置状态：Loaded={Loaded}, EnabledRegions={EnabledRegionCount}, Resolution={ResolutionWidth}x{ResolutionHeight}",
                recognitionRegionConfig.LoadedFromFile,
                recognitionRegionConfig.Regions.Count(region => region.Enabled),
                configResolutionWidth,
                configResolutionHeight);

            var message = "实时任务已启动。";

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

    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        _encounterStatisticsEnabled = isEnabled;
        _settingsLoaded = true;
        _ = SaveEncounterStatisticsEnabledAsync(isEnabled);
    }

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        _autoBattleSettings = NormalizeAutoBattleSettings(settings);
        if (!_autoBattleSettings.IsEnabled)
        {
            ResetAutoBattleBattleState();
        }

        _settingsLoaded = true;
        _ = SaveAutoBattleSettingsAsync(_autoBattleSettings);
    }

    private async Task SaveEncounterStatisticsEnabledAsync(bool isEnabled)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.EncounterStatisticsEnabled, isEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存奇遇统计开关状态失败。");
        }
    }

    private async Task SaveAutoBattleSettingsAsync(AutoBattleSettings settings)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.AutoBattleSettings, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存自动战斗设置失败。");
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellationTokenSource = null;
        Task? captureTask = null;
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
            state = CurrentState;
            _captureCancellationTokenSource = null;
            _captureTask = null;
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
            if (captureTask is not null)
            {
                await captureTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (state is not null)
            {
                _screenCaptureService.Release(state.TargetWindow, state.Options.CaptureMethod);
            }

            cancellationTokenSource?.Dispose();
            _logger.LogInformation("运行任务已停止");
        }
    }

    private async Task CaptureLoopAsync(RuntimeTaskState state, CancellationToken cancellationToken)
    {
        var nextGameStateScanAt = DateTimeOffset.MinValue;
        var nextEncounterScanAt = DateTimeOffset.MinValue;

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

                            if (gameStateScanResult == GameStateScanResult.Battle
                                && EncounterStatisticsEnabled
                                && now >= nextEncounterScanAt)
                            {
                                nextEncounterScanAt = now + EncounterScanInterval;

                                try
                                {
                                    await TryUpdateEncounterStatisticsAsync(state, frame, cancellationToken);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    _logger.LogWarning(ex, "奇遇统计识别失败");
                                }
                            }
                            else if (gameStateScanResult == GameStateScanResult.NonBattle)
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

    private async Task<GameStateScanResult> UpdateGameStateSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var isPetSwitchingVisible = await IsBattlePetSwitchingAsync(state, frame, cancellationToken);
        if (isPetSwitchingVisible)
        {
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                "战斗中 - 切换精灵",
                DateTimeOffset.Now));
            await HandleAutoBattlePetSwitchingAsync(state, cancellationToken);
            ResetAutoBattleSkillSelectionState();
            return GameStateScanResult.Battle;
        }

        _wasAutoBattlePetSwitchingVisible = false;

        var isSkillSelectionVisible = await IsBattleSkillSelectionVisibleAsync(state, frame, cancellationToken);
        if (isSkillSelectionVisible)
        {
            var recoveredEnergy = await TryDetectAutoBattleEnergyShortageAsync(state, frame, cancellationToken);
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                "战斗中 - 技能选择",
                DateTimeOffset.Now));
            if (!recoveredEnergy)
            {
                await HandleAutoBattleSkillSelectionAsync(state, cancellationToken);
            }

            return GameStateScanResult.Battle;
        }

        ResetAutoBattleSkillSelectionState();

        if (await IsBattleChatVisibleAsync(state, frame, cancellationToken))
        {
            await TryDetectAutoBattleEnergyShortageAsync(state, frame, cancellationToken);
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                "战斗中",
                DateTimeOffset.Now));
            return GameStateScanResult.Battle;
        }

        ResetAutoBattleBattleState();
        return await UpdateMagicPointSnapshotAsync(state, frame, cancellationToken);
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
            magicPointMaximum);
    }

    private IReadOnlyList<InfoOverlayCounter> GetCurrentSeasonEncounterCounters()
    {
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            return [];
        }

        return _statisticsService.GetSelectedAccountSeasonEncounters(season.Id)
            .Select(record => new InfoOverlayCounter(
                record.Name,
                record.Count,
                0,
                record.LastCapturedAt))
            .ToList();
    }

    private async Task<bool> IsBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var battleMagicRegion = FindRegion(state.RecognitionRegionConfig, BattleMagicRegionIds);
        if (battleMagicRegion is null || !TemplateExists(MagicPointTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
            battleMagicRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            MagicPointTemplateName,
            MagicPointMatchOptions,
            cancellationToken);
        return result.IsMatch;
    }

    private async Task<bool> IsBattleSkillSelectionVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var battleSkillRegion = FindRegion(state.RecognitionRegionConfig, BattleSkillRegionIds);
        if (battleSkillRegion is null || !TemplateExists(BattleSkillTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
            battleSkillRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            BattleSkillTemplateName,
            BattleSkillMatchOptions,
            cancellationToken);
        return result.IsMatch;
    }

    private async Task<bool> IsBattleChatVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var battleChatRegion = FindRegion(state.RecognitionRegionConfig, BattleChatRegionIds);
        if (battleChatRegion is null || !TemplateExists(BattleChatTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
            battleChatRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            BattleChatTemplateName,
            BattleChatMatchOptions,
            cancellationToken);
        return result.IsMatch;
    }

    private async Task HandleAutoBattleSkillSelectionAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (!_autoBattleSettings.IsEnabled)
        {
            _wasAutoBattleSkillSelectionVisible = true;
            return;
        }

        if (!_wasAutoBattleSkillSelectionVisible)
        {
            _wasAutoBattleSkillSelectionVisible = true;
            _autoBattleSkillSelectionVisibleSince = now;
            _lastAutoBattleSkillSelectionActionAt = null;
            return;
        }

        if (!ShouldRunAutoBattleSkillSelectionAction(now))
        {
            return;
        }

        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var settings = NormalizeAutoBattleSettings(_autoBattleSettings);
            if (!settings.IsEnabled)
            {
                return;
            }

            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗未执行：目标游戏窗口句柄已失效。");
                return;
            }

            AdvanceAutoBattleRoundIfPending();

            var skillKey = GetCurrentAutoBattleSkillKey(settings);
            var sequence = BuildAutoBattleTurnSequence(settings, skillKey);
            if (!_keyboardInputService.TryParseSequence(sequence, out var keyStrokes, out var parseError)
                || keyStrokes.Count == 0)
            {
                _logger.LogWarning(
                    "自动战斗单回合序列无效，已回退为技能键 {SkillKey}。Sequence={Sequence}, Error={Error}",
                    skillKey,
                    sequence,
                    parseError);
                keyStrokes = _keyboardInputService.TryParseSequence(skillKey, out var fallbackStrokes, out _)
                    ? fallbackStrokes
                    : [];
            }

            if (keyStrokes.Count == 0)
            {
                return;
            }

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                keyStrokes,
                AutoBattleKeyboardInputOptions,
                cancellationToken);

            _pendingAutoBattleRoundAdvance = true;
            _lastAutoBattleSkillSelectionActionAt = DateTimeOffset.Now;

            _logger.LogInformation(
                "自动战斗已执行：SkillKey={SkillKey}, Sequence={Sequence}",
                skillKey,
                sequence);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动战斗技能释放失败。");
        }
        finally
        {
            _autoBattleActionLock.Release();
        }
    }

    private async Task HandleAutoBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        AdvanceAutoBattleRoundIfPending();

        if (!_autoBattleSettings.IsEnabled)
        {
            _wasAutoBattlePetSwitchingVisible = true;
            return;
        }

        if (_wasAutoBattlePetSwitchingVisible)
        {
            return;
        }

        _wasAutoBattlePetSwitchingVisible = true;
        if (!TemplateExists(BattleSpaceTemplateName))
        {
            _logger.LogWarning("自动战斗换精灵未执行：未找到 {TemplateName}。", BattleSpaceTemplateName);
            return;
        }

        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗换精灵未执行：目标游戏窗口句柄已失效。");
                return;
            }

            for (var slot = 1; slot <= 6; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var slotKey = slot.ToString();
                await _keyboardInputService.SendSequenceAsync(
                    state.TargetWindow.Hwnd,
                    slotKey,
                    AutoBattleKeyboardInputOptions,
                    cancellationToken);

                await Task.Delay(AutoBattlePetSwitchProbeDelay, cancellationToken);

                using var frame = _screenCaptureService.Capture(state.TargetWindow, state.Options.CaptureMethod);
                if (frame is null || !await IsBattleSpaceVisibleAsync(state, frame, cancellationToken))
                {
                    continue;
                }

                await _keyboardInputService.SendSequenceAsync(
                    state.TargetWindow.Hwnd,
                    "Space",
                    AutoBattleKeyboardInputOptions,
                    cancellationToken);
                _logger.LogInformation("自动战斗已切换精灵：Slot={Slot}", slot);
                return;
            }

            _logger.LogWarning("自动战斗换精灵失败：已尝试 1-6，但未检测到 battle-space 确认提示。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动战斗换精灵失败。");
        }
        finally
        {
            _autoBattleActionLock.Release();
        }
    }

    private async Task<bool> TryDetectAutoBattleEnergyShortageAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_autoBattleSettings.IsEnabled || !_pendingAutoBattleRoundAdvance)
        {
            return false;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipEnergyRegionIds,
            cancellationToken);
        if (!IsEnergyShortageTip(tipText))
        {
            return false;
        }

        if (!await TrySendAutoBattleEnergyRecoveryAsync(state, cancellationToken))
        {
            return false;
        }

        _pendingAutoBattleRoundAdvance = false;
        _logger.LogInformation("自动战斗检测到能量不足，已立即按 X 回复能量。");
        return true;
    }

    private async Task<bool> TrySendAutoBattleEnergyRecoveryAsync(
        RuntimeTaskState state,
        CancellationToken cancellationToken)
    {
        if (!await _autoBattleActionLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (!_keyboardInputService.IsWindowAvailable(state.TargetWindow.Hwnd))
            {
                _logger.LogWarning("自动战斗回能未执行：目标游戏窗口句柄已失效。");
                return false;
            }

            await _keyboardInputService.SendSequenceAsync(
                state.TargetWindow.Hwnd,
                "X",
                AutoBattleKeyboardInputOptions,
                cancellationToken);
            _lastAutoBattleSkillSelectionActionAt = DateTimeOffset.Now;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动战斗回能失败。");
            return false;
        }
        finally
        {
            _autoBattleActionLock.Release();
        }
    }

    private async Task<bool> IsBattleSpaceVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var battleSpaceRegion = FindRegion(state.RecognitionRegionConfig, BattleSpaceRegionIds);
        if (battleSpaceRegion is null || !TemplateExists(BattleSpaceTemplateName))
        {
            return false;
        }

        var frameRegion = ToFrameRegion(
            battleSpaceRegion,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return false;
        }

        var result = await _imageMatchingService.MatchAsync(
            frame,
            frameRegion,
            BattleSpaceTemplateName,
            BattleSpaceMatchOptions,
            cancellationToken);
        return result.IsMatch;
    }

    private string GetCurrentAutoBattleSkillKey(AutoBattleSettings settings)
    {
        var roundOrder = ParseAutoBattleRoundOrder(settings.RoundOrder);
        if (_autoBattleRoundIndex >= roundOrder.Count)
        {
            _autoBattleRoundIndex = 0;
        }

        return roundOrder[_autoBattleRoundIndex];
    }

    private static IReadOnlyList<string> ParseAutoBattleRoundOrder(string roundOrder)
    {
        if (string.IsNullOrWhiteSpace(roundOrder))
        {
            return AutoBattleDefaultRoundOrder;
        }

        var keys = roundOrder
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace(';', ',')
            .Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(key => key.ToUpperInvariant())
            .Where(key => key is "1" or "2" or "3" or "4" or "X")
            .ToArray();
        return keys.Length == 0 ? AutoBattleDefaultRoundOrder : keys;
    }

    private static string BuildAutoBattleTurnSequence(AutoBattleSettings settings, string skillKey)
    {
        var turnSequence = string.IsNullOrWhiteSpace(settings.TurnSequence)
            ? AutoBattleSettings.DefaultTurnSequence
            : settings.TurnSequence.Trim();

        return turnSequence.Contains(AutoBattleSkillPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? turnSequence.Replace(AutoBattleSkillPlaceholder, skillKey, StringComparison.OrdinalIgnoreCase)
            : turnSequence;
    }

    private void AdvanceAutoBattleRoundIfPending()
    {
        if (!_pendingAutoBattleRoundAdvance)
        {
            return;
        }

        _autoBattleRoundIndex++;
        _pendingAutoBattleRoundAdvance = false;
    }

    private bool ShouldRunAutoBattleSkillSelectionAction(DateTimeOffset now)
    {
        if (!_autoBattleSkillSelectionVisibleSince.HasValue)
        {
            _autoBattleSkillSelectionVisibleSince = now;
            return false;
        }

        if (now - _autoBattleSkillSelectionVisibleSince.Value < AutoBattleSkillSelectionActionDelay)
        {
            return false;
        }

        if (!_lastAutoBattleSkillSelectionActionAt.HasValue)
        {
            return true;
        }

        return now - _lastAutoBattleSkillSelectionActionAt.Value >= AutoBattleSkillSelectionRetryDelay;
    }

    private static bool IsEnergyShortageTip(string tipText)
    {
        if (string.IsNullOrWhiteSpace(tipText))
        {
            return false;
        }

        var normalized = new string(tipText.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return normalized.Contains("能量不足", StringComparison.Ordinal)
            || TextMatchingHelper.IsSimilar(tipText, "能量不足", 0.65, out _);
    }

    private void ResetAutoBattleBattleState()
    {
        _autoBattleRoundIndex = 0;
        ResetAutoBattleSkillSelectionState();
        _wasAutoBattlePetSwitchingVisible = false;
        _pendingAutoBattleRoundAdvance = false;
    }

    private void ResetAutoBattleSkillSelectionState()
    {
        _wasAutoBattleSkillSelectionVisible = false;
        _autoBattleSkillSelectionVisibleSince = null;
        _lastAutoBattleSkillSelectionActionAt = null;
    }

    private async Task<GameStateScanResult> UpdateMagicPointSnapshotAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var magicPointRegion = FindRegion(state.RecognitionRegionConfig, MagicPointRegionIds);
        if (magicPointRegion is null)
        {
            UpdateRecognizedInfoOverlaySnapshot(CreateInfoOverlaySnapshot(
                "等待 magic-point 区域",
                DateTimeOffset.Now));
            return GameStateScanResult.NonBattle;
        }

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

        var magicPointCount = 0;
        foreach (var slotRegion in SplitMagicPointSlots(frameRegion))
        {
            var result = await _imageMatchingService.MatchAsync(
                frame,
                slotRegion,
                MagicPointTemplateName,
                MagicPointMatchOptions,
                cancellationToken);
            if (result.IsMatch)
            {
                magicPointCount++;
            }
        }

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

    private async Task TryUpdateEncounterStatisticsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null || string.IsNullOrWhiteSpace(season.TipText))
        {
            return;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRelieveRegionIds,
            cancellationToken);

        if (!TextMatchingHelper.IsSimilar(
                tipText,
                season.TipText,
                season.MatchThreshold,
                out var similarity))
        {
            return;
        }

        var enemyNameText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken);
        var enemyName = TextMatchingHelper.CleanSpiritName(enemyNameText);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            _logger.LogDebug("已匹配奇遇提示，但 battle-enemy-name 区域未识别到精灵名。相似度：{Similarity:P1}", similarity);
            return;
        }

        if (!TryReserveEncounterRecord(season.Id, enemyName, DateTimeOffset.Now))
        {
            return;
        }

        await _statisticsService.RecordEncounterAsync(season, enemyName, DateTimeOffset.Now);
        _logger.LogInformation(
            "检测到赛季奇遇：Season={SeasonId}, Type={EncounterType}, Spirit={SpiritName}, TipSimilarity={Similarity:P1}",
            season.Id,
            season.EncounterTypeName,
            enemyName,
            similarity);
    }

    private async Task<string> RecognizeRegionTextAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        IReadOnlyList<string> regionAliases,
        CancellationToken cancellationToken)
    {
        var region = FindRegion(state.RecognitionRegionConfig, regionAliases);
        if (region is null)
        {
            return string.Empty;
        }

        var frameRegion = ToFrameRegion(
            region,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        if (frameRegion.Width <= 0 || frameRegion.Height <= 0)
        {
            return string.Empty;
        }

        var recognitionMethod = _textRecognitionService
            .GetMethods()
            .FirstOrDefault(method => method.Method == state.Options.TextRecognitionMethod && method.IsAvailable);
        if (recognitionMethod is null)
        {
            return string.Empty;
        }

        var imageBytes = await EncodeFrameRegionPngAsync(frame, frameRegion, cancellationToken);
        var result = await _textRecognitionService.RecognizeAsync(
            imageBytes,
            recognitionMethod.Method,
            cancellationToken);
        return result.Text;
    }

    private bool TryReserveEncounterRecord(string seasonId, string spiritName, DateTimeOffset now)
    {
        lock (_encounterRecordLock)
        {
            if (string.Equals(_lastRecordedEncounterSeasonId, seasonId, StringComparison.OrdinalIgnoreCase)
                && (_hasActiveEncounterRecord || now - _lastRecordedEncounterAt < EncounterDuplicateSuppressWindow))
            {
                _logger.LogDebug(
                    "奇遇统计冷却中，本次识别已忽略。LastSpirit={LastSpiritName}, CurrentSpirit={CurrentSpiritName}",
                    _lastRecordedEncounterName,
                    spiritName);
                return false;
            }

            _lastRecordedEncounterSeasonId = seasonId;
            _lastRecordedEncounterName = spiritName;
            _lastRecordedEncounterAt = now;
            _hasActiveEncounterRecord = true;
            return true;
        }
    }

    private void ResetEncounterRecordSuppression()
    {
        lock (_encounterRecordLock)
        {
            _hasActiveEncounterRecord = false;
        }
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

    private static RecognitionRegion? FindRegion(
        RecognitionRegionConfig config,
        IReadOnlyList<string> aliases)
    {
        return config.Regions.FirstOrDefault(region => IsRegionMatch(region, aliases));
    }

    private static bool IsRegionMatch(RecognitionRegion region, IReadOnlyList<string> aliases)
    {
        if (!region.Enabled || string.IsNullOrWhiteSpace(region.Id))
        {
            return false;
        }

        var id = region.Id.Trim();
        return aliases.Any(alias =>
            string.Equals(id, alias, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith($"{alias}-", StringComparison.OrdinalIgnoreCase));
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

    private static AutoBattleSettings NormalizeAutoBattleSettings(AutoBattleSettings? settings)
    {
        var normalized = settings?.Clone() ?? AutoBattleSettings.CreateDefault();
        if (string.IsNullOrWhiteSpace(normalized.RoundOrder))
        {
            normalized.RoundOrder = AutoBattleSettings.DefaultRoundOrder;
        }

        if (string.IsNullOrWhiteSpace(normalized.TurnSequence))
        {
            normalized.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        }

        return normalized;
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

    private enum GameStateScanResult
    {
        Battle,
        NonBattle,
        UnrecognizedPending
    }
}
