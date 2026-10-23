using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Input;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Services.IndependentTasks;

public sealed class ManualChallengeRunner(
    IScreenCaptureService capture,
    IKeyboardInputService keyboard,
    IMouseInputService mouse,
    ManualChallengeScreenRecognizer recognizer,
    AutoBattleInputExecutor battleInput,
    IRecognitionOverlayService overlay,
    ILogger<ManualChallengeRunner> logger) : IManualChallengeRunner
{
    public async Task RunLegendaryAsync(RuntimeTaskState state, string targetName, int runCount,
        bool useLegendaryMark, AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress, CancellationToken cancellationToken)
        => _ = await RunCoreAsync(state, null, runCount, battleSettings, progress, cancellationToken, targetName, useLegendaryMark);

    public async Task RunAsync(RuntimeTaskState state, FlowerSeedOption target, int runCount, AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => _ = await RunCoreAsync(state, target, runCount, battleSettings, progress, cancellationToken);

    public Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => RunCoreAsync(state, null, 0, state.Options.AutoBattleSettings, progress, cancellationToken);

    private async Task<IReadOnlyList<FlowerSeedOption>> RunCoreAsync(RuntimeTaskState state, FlowerSeedOption? target,
        int runCount, AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress, CancellationToken token, string? legendaryTarget = null, bool useLegendaryMark = true)
    {
        var legendary = legendaryTarget is not null;
        var scanning = target is null && !legendary;
        var flow = new ManualChallengeFlow(target, runCount, legendaryTarget, useLegendaryMark);
        var settings = AutoBattleSettingsRules.Normalize(battleSettings);
        if (!scanning)
        {
            settings.IsEnabled = true;
            settings.ReleaseSequence = legendary ? settings.LegendaryReleaseSequence : settings.FlowerSeedReleaseSequence;
            settings.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        }
        var battle = new AutoBattleController();
        var battleNumber = 0;
        var window = state.TargetWindow;
        var inputMethod = settings.KeyboardInputMethod;
        keyboard.EnsureReady(inputMethod);
        var lastScene = ManualChallengeScene.Unknown;
        (ManualChallengeScene Scene, BattleScreen? Screen, ManualChallengeAction Action)? lastObservation = null;
        var creatureName = legendaryTarget ?? target?.Name ?? string.Empty;
        var lastProgress = new IndependentTaskProgress("准备启动");
        var lastAdvance = DateTimeOffset.UtcNow;
        var lastAction = DateTimeOffset.MinValue;
        var lastActionScene = ManualChallengeScene.Unknown;
        var lastFrameTime = DateTimeOffset.MinValue;
        var minimumFrameAt = DateTimeOffset.UtcNow;
        CapturedFrame? pendingFrame = null;
        logger.LogInformation("手册挑战启动：{Target}，输入方式：{Method}", legendaryTarget ?? target?.DisplayName ?? "扫描全部花种", inputMethod);

        try
        {
            while (true)
            {
                using var frame = await CaptureNextFrameAsync();
                _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window,
                    out var clientX, out var clientY, out var width, out var height);
                List<(RecognitionRegion Region, string Text)> previews = [];
                var screen = await recognizer.ReadAsync(frame, clientX, clientY, width, height, token,
                    state.Options.RecognitionOverlayEnabled ? (region, text) => previews.Add((region, text)) : null, legendary);
                if (target is not null && screen.Scene == ManualChallengeScene.Map && !string.IsNullOrWhiteSpace(screen.Text))
                    creatureName = screen.Text;
                if (screen.Scene != ManualChallengeScene.Unknown && screen.Scene != lastScene)
                {
                    lastScene = screen.Scene;
                    lastAdvance = DateTimeOffset.UtcNow;
                    lastProgress = new(Stage(screen.Scene));
                }
                var count = flow.Options.Count;
                var completedCount = flow.CompletedCount;
                var failures = flow.ConsecutiveFailures;
                var decision = flow.Next(screen, height);
                var observation = (screen.Scene, screen.BattleScreen, decision.Action);
                if (observation != lastObservation)
                {
                    lastObservation = observation;
                    logger.LogDebug("挑战状态：{Scene}/{Screen}，动作 {Action}，第 {Battle} 场，战斗有效 {Active}，回合 {Turn}，匹配 {Template}，得分 {Score:F3}",
                        screen.Scene, screen.BattleScreen, decision.Action, flow.BattleNumber, flow.IsBattleActive,
                        battle.TurnNumber, screen.Button?.TemplatePath, screen.Button?.Score);
                }
                if (flow.Options.Count > count) lastAdvance = DateTimeOffset.UtcNow;
                if (screen.Scene == ManualChallengeScene.Battle && flow.IsBattleActive
                    || flow.CompletedCount != completedCount || flow.ConsecutiveFailures != failures)
                    lastAdvance = DateTimeOffset.UtcNow;
                if (flow.ConsecutiveFailures != failures)
                    logger.LogWarning("挑战失败，原地重试：连续失败 {Failures}/3，已成功 {Completed}/{Total} 次，触发画面 {Scene}",
                        flow.ConsecutiveFailures, flow.CompletedCount, runCount, screen.Scene);
                if (flow.BattleNumber != battleNumber)
                {
                    battleNumber = flow.BattleNumber;
                    battle.ResetBattle();
                }
                AutoBattlePlan? plan = null;
                var switchingPet = decision.Action == ManualChallengeAction.Battle
                    && screen.BattleScreen is BattleScreen.PetSwitching or BattleScreen.PetSwitchConfirmation;
                if (decision.Action == ManualChallengeAction.Battle && screen.BattleScreen == BattleScreen.SkillSelection)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (battle.Phase != AutoBattlePhase.SkillSelection)
                    {
                        var turn = battle.BeginSkillSelection(settings, now);
                        battle.ConfirmSelectionReady(turn.Id);
                    }
                    if (battle.ShouldRecoverAfterSkillFailure(settings, now))
                        plan = new(AutoBattleAction.EnergyRecovery, "X", "技能未离开选择界面，回能 X，原技能延后", "X");
                    else if (battle.CanAct(settings, now))
                        plan = battle.PlanSkillSelection(settings, false, now);
                }
                else if (screen.Scene != ManualChallengeScene.Unknown)
                    battle.CompleteSkillSelection();
                if (switchingPet && battle.Phase != AutoBattlePhase.PetSwitching)
                    battle.BeginPetSwitching(settings);
                else if (screen.Scene != ManualChallengeScene.Unknown && !switchingPet)
                    battle.ObservePetSwitching(false);
                if (decision.Action == ManualChallengeAction.Capture)
                    plan = new(AutoBattleAction.Capture, "1, Space", "使用专属球捕捉", "1, Space");
                if (screen.Scene == ManualChallengeScene.FlowerList)
                {
                    if (!flow.IsTopConfirmed) previews.Clear();
                    else for (var i = 0; i < previews.Count; i++)
                    {
                        previews[i].Region.Id = $"花种 {screen.Rows[i].Number}";
                        previews[i].Region.IsMatched = screen.Rows[i].HasMatched;
                    }
                }
                else if (screen.Scene == ManualChallengeScene.LegendaryList)
                {
                    for (var i = 0; i < previews.Count; i++)
                        previews[i].Region.IsMatched = !string.IsNullOrWhiteSpace(screen.Rows[i].Name);
                }
                else foreach (var preview in previews)
                    preview.Region.IsMatched = !string.IsNullOrWhiteSpace(screen.Text);

                if (state.Options.RecognitionOverlayEnabled)
                {
                    overlay.Show(state, new RecognitionRegionConfig
                    {
                        ResolutionWidth = width, ResolutionHeight = height,
                        Regions = previews.Select(preview => new RecognitionRegion
                        {
                            Id = preview.Region.Id, X = preview.Region.X - clientX, Y = preview.Region.Y - clientY,
                            Width = preview.Region.Width, Height = preview.Region.Height, IsMatched = preview.Region.IsMatched
                        }).ToList()
                    });
                    foreach (var preview in previews) overlay.ShowOcrResult(preview.Region.Id, preview.Text);
                }
                var recognized = screen.Scene switch
                {
                    ManualChallengeScene.Unknown => "正在确认当前界面",
                    ManualChallengeScene.FlowerList when !flow.IsTopConfirmed => "正在定位列表顶部",
                    ManualChallengeScene.FlowerList => "当前花种：" + string.Join("、",
                        screen.Rows.Select(row => flow.Options[row.Number - 1].DisplayName)),
                    ManualChallengeScene.Map when target is not null => "当前花种：" +
                        (target with { Name = string.IsNullOrWhiteSpace(screen.Text) ? target.Name : screen.Text }).DisplayName,
                    ManualChallengeScene.Interaction => string.IsNullOrWhiteSpace(screen.Text)
                        ? "正在确认交互选项" : $"当前选项：{screen.Text}",
                    ManualChallengeScene.LegendaryList => "当前精灵：" + string.Join("、", screen.Rows.Select(row => row.Name)),
                    _ => $"当前界面：{Stage(screen.Scene)}"
                };
                var operation = flow.StopReason is not null ? "退出并停止：" + flow.StopReason : decision.Action switch
                {
                    ManualChallengeAction.None => lastProgress.Operation,
                    ManualChallengeAction.Complete => scanning
                        ? $"扫描完成，共{flow.Options.Count}个花种" : $"挑战完成，成功{flow.CompletedCount}次，已返回大世界",
                    ManualChallengeAction.Battle when switchingPet => "切换精灵",
                    ManualChallengeAction.Battle => plan is not null ? InfoOverlayIslandPresentation.BattleInputTitle(plan)
                        : screen.BattleScreen != BattleScreen.SkillSelection ? "等待行动"
                        : battle.CurrentTurn?.Action == AutoBattleAction.EnergyRecovery ? "等待重试" : "准备技能",
                    ManualChallengeAction.Capture => "捕捉",
                    _ => Operation(decision.Action, screen.Scene)
                };
                Publish(lastProgress with { Operation = operation,
                    IsBattleWaiting = flow.IsBattleActive && plan is null && !switchingPet
                        && screen.Scene is ManualChallengeScene.Battle or ManualChallengeScene.Unknown,
                    Recognition = scanning ? recognized
                    : screen.Scene is ManualChallengeScene.Battle or ManualChallengeScene.Capture or ManualChallengeScene.Rewards or ManualChallengeScene.Result
                        ? $"{creatureName} · 成功 {flow.CompletedCount}/{runCount}"
                        : $"成功 {flow.CompletedCount}/{runCount} 次 · {recognized}" });
                if (decision.Action == ManualChallengeAction.Stop)
                    throw new InvalidOperationException(flow.StopReason);
                if (decision.Action == ManualChallengeAction.Complete)
                    return flow.Options.ToArray();

                // 当前截图的结果先显示；输入改变画面后不能继续叠加这一帧的框。
                var previewEnabled = state.Options.RecognitionOverlayEnabled;
                if (previewEnabled) await Task.Delay(180, token);
                if (!keyboard.IsWindowForeground(window.Hwnd)) continue;
                if (decision.Action != ManualChallengeAction.None
                    && (decision.Action != ManualChallengeAction.Battle || plan is not null || switchingPet)
                    && (decision.Action is ManualChallengeAction.Battle or ManualChallengeAction.Click || screen.Scene != lastActionScene
                        || DateTimeOffset.UtcNow - lastAction >= TimeSpan.FromMilliseconds(700)))
                {
                    try
                    {
                        if (state.Options.RecognitionOverlayEnabled)
                            overlay.Show(state, new RecognitionRegionConfig { ResolutionWidth = width, ResolutionHeight = height });
                        var nativeWidth = window.ClientWidth > 0 ? window.ClientWidth : width;
                        var nativeHeight = window.ClientHeight > 0 ? window.ClientHeight : height;
                        switch (decision.Action)
                        {
                            case ManualChallengeAction.OpenManual:
                            case ManualChallengeAction.Click:
                                await ActUntilDisappearsAsync(decision.Action, screen.Scene,
                                    decision.Button ?? screen.Button!, clientX, clientY, width, height);
                                break;
                            case ManualChallengeAction.Escape:
                                await ActUntilDisappearsAsync(decision.Action, screen.Scene,
                                    screen.Button ?? new ImageMatchResult(true, 1, 0, 0, 1, 1, "escape"), clientX, clientY, width, height);
                                break;
                            case ManualChallengeAction.ScrollUp:
                            case ManualChallengeAction.ScrollDown:
                                var row = screen.Rows[screen.Rows.Count / 2];
                                await mouse.ScrollAsync(window.Hwnd, (int)Math.Round(nativeWidth * .23),
                                    (int)Math.Round((row.Button.Y + row.Button.Height / 2d - clientY) * nativeHeight / height),
                                    decision.Action == ManualChallengeAction.ScrollUp ? 3600 : -720, inputMethod, token);
                                flow.RecordScroll(screen, height);
                                break;
                            case ManualChallengeAction.Approach:
                                await SendKey("W", 200);
                                break;
                            case ManualChallengeAction.SelectChallenge:
                                await mouse.ScrollAtCurrentPositionAsync(window.Hwnd, 120, inputMethod, token);
                                break;
                            case ManualChallengeAction.Interact:
                                await SendKey("F", settings.KeyboardHoldDurationMs);
                                break;
                            case ManualChallengeAction.Battle when switchingPet:
                                await SwitchPetAsync(screen.BattleScreen!.Value);
                                break;
                            case ManualChallengeAction.Battle:
                            case ManualChallengeAction.Capture:
                                if (!await battleInput.ExecuteAsync(window.Hwnd, settings, plan!, token,
                                        (sequence, completed) =>
                                        {
                                            Publish(lastProgress with { Operation = InfoOverlayIslandPresentation.BattleInputTitle(plan!), IsBattleWaiting = false });
                                            logger.LogDebug("挑战按键：第 {Battle} 场，第 {Turn} 回合，{Action}，序列 {Sequence}，{Status}",
                                                battleNumber, battle.TurnNumber, plan!.Action, sequence, completed ? "发送完成" : "开始发送");
                                        }))
                                    break;
                                if (decision.Action == ManualChallengeAction.Battle && battle.CurrentTurn is { } currentTurn)
                                    battle.RecordAction(currentTurn.Id, plan!.Action, DateTimeOffset.UtcNow);
                                logger.LogInformation("手册挑战：第 {Battle} 场，{Action}，序列 {Sequence}",
                                    battleNumber, plan!.Description, plan.Sequence);
                                break;
                        }
                        lastAction = DateTimeOffset.UtcNow;
                        lastActionScene = screen.Scene;
                        minimumFrameAt = lastAction;
                        // 滚轮动画结束后再比较行位置，避免把尚未更新的帧当作边界。
                        if (decision.Action is ManualChallengeAction.ScrollUp or ManualChallengeAction.ScrollDown)
                        {
                            minimumFrameAt = lastAction.AddMilliseconds(520);
                            await Task.Delay(520, token);
                        }
                    }
                    catch (InvalidOperationException) when (!keyboard.IsWindowForeground(window.Hwnd))
                    {
                        minimumFrameAt = DateTimeOffset.UtcNow;
                        Publish(lastProgress with { Operation = "已暂停，等待切回游戏", IsBattleWaiting = false });
                    }
                }
                if (!previewEnabled) await Task.Delay(180, token);
            }
        }
        finally
        {
            pendingFrame?.Dispose();
            overlay.Hide();
        }

        async Task<CapturedFrame> CaptureNextFrameAsync(Stopwatch? actionTimer = null, string? actionDescription = null)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!keyboard.IsWindowAvailable(window.Hwnd))
                    throw new InvalidOperationException("游戏窗口已关闭。");
                if (!keyboard.IsWindowForeground(window.Hwnd))
                {
                    actionTimer?.Stop();
                    pendingFrame?.Dispose();
                    pendingFrame = null;
                    overlay.Hide();
                    Publish(lastProgress with { Operation = "已暂停，等待切回游戏", IsBattleWaiting = false });
                    await Task.Delay(400, token);
                    lastAdvance = DateTimeOffset.UtcNow;
                    minimumFrameAt = lastAdvance;
                    continue;
                }
                actionTimer?.Start();
                if (actionTimer?.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"{actionDescription}：10 秒内未确认原界面目标消失。");
                if (DateTimeOffset.UtcNow - lastAdvance > TimeSpan.FromSeconds(90))
                    throw new TimeoutException($"等待画面匹配超时，最后确认步骤：{lastProgress.Stage}。");
                if (pendingFrame is { } confirmed)
                {
                    pendingFrame = null;
                    return confirmed;
                }
                var frame = await Task.Run(() => capture.Capture(window, state.Options.CaptureMethod), token);
                if (frame is null)
                {
                    Publish(lastProgress with { Recognition = "正在读取游戏画面" });
                    await Task.Delay(200, token);
                    continue;
                }
                if (frame.CapturedAt <= lastFrameTime || frame.CapturedAt < minimumFrameAt)
                {
                    frame.Dispose();
                    await Task.Delay(120, token);
                    continue;
                }
                lastFrameTime = frame.CapturedAt;
                return frame;
            }
        }

        async Task SwitchPetAsync(BattleScreen phase)
        {
            for (var slot = 1; slot <= 6; slot++)
            {
                if (!keyboard.IsWindowForeground(window.Hwnd)) return;
                if (phase == BattleScreen.PetSwitching)
                {
                    Publish(lastProgress with { Operation = $"切换精灵：选择第 {slot} 只", IsBattleWaiting = false });
                    await SendKey(slot.ToString(), settings.KeyboardHoldDurationMs);
                    minimumFrameAt = DateTimeOffset.UtcNow;
                    await Task.Delay(1500, token);
                    phase = await ReadSwitchScreenAsync();
                    if (phase is not (BattleScreen.PetSwitching or BattleScreen.PetSwitchConfirmation)) return;
                }
                var timer = Stopwatch.StartNew();
                do
                {
                    Publish(lastProgress with { Operation = "切换精灵：按 Space 确认", IsBattleWaiting = false });
                    await SendKey("Space", settings.KeyboardHoldDurationMs);
                    minimumFrameAt = DateTimeOffset.UtcNow;
                    logger.LogDebug("挑战换精灵：已发送 Space，等待确认界面消失");
                    await Task.Delay(1500, token);
                    phase = await ReadSwitchScreenAsync(timer);
                } while (phase == BattleScreen.PetSwitchConfirmation);
                if (phase != BattleScreen.PetSwitching) return;
            }
            throw new InvalidOperationException("换精灵失败：已尝试 1-6 并按 Space 确认，仍处于切换界面。");

            async Task<BattleScreen> ReadSwitchScreenAsync(Stopwatch? timer = null)
            {
                using var next = await CaptureNextFrameAsync(timer, "换精灵确认");
                _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(next, window,
                    out var x, out var y, out var w, out var h);
                var nextScreen = await recognizer.ReadAsync(next, x, y, w, h, token, legendary: legendary);
                if (nextScreen.BattleScreen is not (BattleScreen.PetSwitching or BattleScreen.PetSwitchConfirmation))
                    pendingFrame = next.AddReference();
                return nextScreen.BattleScreen ?? BattleScreen.Unknown;
            }
        }

        async Task ActUntilDisappearsAsync(ManualChallengeAction action, ManualChallengeScene scene, ImageMatchResult initial,
            int clientX, int clientY, int width, int height)
        {
            var timer = Stopwatch.StartNew();
            var operation = flow.StopReason is not null ? "退出并停止：" + flow.StopReason : Operation(action, scene);
            var rowY = (initial.Y + initial.Height / 2d - clientY) / height;
            ImageMatchResult? button = initial;
            var attempts = 0;
            while (button is not null)
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"{operation}：输入 {attempts} 次后目标仍未消失（{initial.TemplatePath}）。");
                // F3 是开关按键，只发一次；鼠标点击可以在原目标仍存在时重试。
                if (keyboard.IsWindowForeground(window.Hwnd) && (action == ManualChallengeAction.Click || attempts == 0))
                {
                    Publish(lastProgress with { Operation = operation });
                    if (action == ManualChallengeAction.OpenManual)
                    {
                        logger.LogDebug("手册 F3：开始发送，原目标 {Template}", initial.TemplatePath);
                        await SendKey("F3", settings.KeyboardHoldDurationMs);
                        minimumFrameAt = DateTimeOffset.UtcNow;
                        attempts++;
                        logger.LogDebug("手册 F3：发送完成，等待大世界 HUD 消失，输入完成时间 {InputCompletedAt:O}", minimumFrameAt);
                    }
                    else if (action == ManualChallengeAction.Escape)
                    {
                        await SendKey("Esc", settings.KeyboardHoldDurationMs);
                        minimumFrameAt = DateTimeOffset.UtcNow;
                        attempts++;
                    }
                    else
                    {
                        var nativeWidth = window.ClientWidth > 0 ? window.ClientWidth : width;
                        var nativeHeight = window.ClientHeight > 0 ? window.ClientHeight : height;
                        var x = (int)Math.Round((button.X + button.Width / 2d - clientX) * nativeWidth / width);
                        var y = (int)Math.Round((button.Y + button.Height / 2d - clientY) * nativeHeight / height);
                        await mouse.ClickAsync(window.Hwnd, x, y, inputMethod, token);
                        minimumFrameAt = DateTimeOffset.UtcNow;
                        logger.LogDebug("挑战点击：{Operation}，第 {Attempt} 次，模板 {Template}，得分 {Score:F3}，客户区坐标 ({X},{Y})",
                            operation, ++attempts, button.TemplatePath, button.Score, x, y);
                    }
                }
                await Task.Delay(700, token);
                using var next = await CaptureNextFrameAsync(timer, $"{operation}（{initial.TemplatePath}，已输入 {attempts} 次）");
                _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(next, window,
                    out clientX, out clientY, out width, out height);
                if (action == ManualChallengeAction.Escape)
                {
                    var nextScreen = await recognizer.ReadAsync(next, clientX, clientY, width, height, token, legendary: legendary);
                    button = nextScreen.Scene == scene ? initial : null;
                }
                else button = await recognizer.FindClickTargetAsync(next, scene, initial.TemplatePath, rowY,
                    clientX, clientY, width, height, token, legendary);
                if (action == ManualChallengeAction.OpenManual)
                    logger.LogDebug("手册 F3 确认：截图时间 {CapturedAt:O}，HUD仍存在 {Visible}，得分 {Score:F3}",
                        next.CapturedAt, button is not null, button?.Score);
                // 消失这一帧也交给后续流程；WGC 静止画面未必立即产生另一张新帧。
                if (button is null) pendingFrame = next.AddReference();
            }
            lastAdvance = DateTimeOffset.UtcNow;
            logger.LogDebug("挑战动作完成：{Operation}，目标已消失，共输入 {Attempts} 次", operation, attempts);
        }

        Task SendKey(string key, int holdDuration)
            => keyboard.SendSequenceAsync(window.Hwnd, key,
                new KeyboardInputOptions { Method = inputMethod, HoldDurationMs = holdDuration, IntervalMs = 0 }, token);

        void Publish(IndependentTaskProgress update)
        {
            update = update with { CreatureName = creatureName };
            if (update == lastProgress) return;
            lastProgress = update;
            progress(update);
        }
    }

    private static string Stage(ManualChallengeScene scene) => scene switch
    {
        ManualChallengeScene.World => "大世界",
        ManualChallengeScene.Manual => "魔法师手册 - 学院作业",
        ManualChallengeScene.ChallengePage => "魔法师手册 - 挑战",
        ManualChallengeScene.FlowerList => "稀兽花种列表",
        ManualChallengeScene.Map => "挑战传送地图",
        ManualChallengeScene.Interaction => "挑战交互菜单",
        ManualChallengeScene.LegendaryList => "传说精灵列表",
        ManualChallengeScene.LegendaryConfirmation => "传说精灵挑战详情",
        ManualChallengeScene.InsufficientContractMark => "契约印记不足提示",
        ManualChallengeScene.CaptureConfirmation => "选择捕捉印记",
        ManualChallengeScene.LegendaryRewards => "印记奖励",
        ManualChallengeScene.Confirmation => "确认花种挑战",
        ManualChallengeScene.Preparation => "挑战准备",
        ManualChallengeScene.MedalConfirmation => "确认奖牌提示",
        ManualChallengeScene.Battle => "战斗",
        ManualChallengeScene.Capture => "捕捉",
        ManualChallengeScene.Rewards => "获得奖励",
        ManualChallengeScene.Result => "挑战结果",
        _ => "等待画面匹配"
    };

    private static string Operation(ManualChallengeAction action, ManualChallengeScene scene) => action switch
    {
        ManualChallengeAction.OpenManual => "按F3打开手册",
        ManualChallengeAction.ScrollUp => "回到花种列表顶部",
        ManualChallengeAction.ScrollDown => "向下滚动花种列表",
        ManualChallengeAction.Approach => "按W靠近挑战目标",
        ManualChallengeAction.SelectChallenge => "滚轮切换到挑战选项",
        ManualChallengeAction.Interact => "按F选择挑战",
        ManualChallengeAction.Escape => scene == ManualChallengeScene.LegendaryRewards ? "按Esc关闭奖励" : "按Esc退出挑战",
        ManualChallengeAction.Click => scene switch
        {
            ManualChallengeScene.Manual => "打开挑战页面",
            ManualChallengeScene.ChallengePage => "打开目标挑战列表",
            ManualChallengeScene.FlowerList => "传送到目标花种",
            ManualChallengeScene.LegendaryList => "传送到目标传说精灵",
            ManualChallengeScene.LegendaryConfirmation => "点击开始挑战",
            ManualChallengeScene.InsufficientContractMark => "确认继续，使用传说印记捕捉",
            ManualChallengeScene.CaptureConfirmation => "选择印记并确认捕捉",
            ManualChallengeScene.Map => "点击地图传送",
            ManualChallengeScene.Confirmation => "点击挑战",
            ManualChallengeScene.Preparation => "点击开始",
            ManualChallengeScene.MedalConfirmation => "确认奖牌提示，点击开始",
            ManualChallengeScene.Rewards => "关闭奖励结算",
            ManualChallengeScene.Result => "选择再次挑战或退出",
            _ => "点击匹配按钮"
        },
        _ => "等待界面就绪"
    };
}
