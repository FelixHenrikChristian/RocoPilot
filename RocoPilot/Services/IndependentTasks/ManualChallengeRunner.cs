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
using RocoPilot.Configuration;
using RocoPilot.Services.Recognition;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Services.IndependentTasks;

public sealed class ManualChallengeRunner(
    IScreenCaptureService capture,
    IKeyboardInputService keyboard,
    IMouseInputService mouse,
    ManualChallengeScreenRecognizer recognizer,
    BattleScreenRecognizer battleScreen,
    AutoBattleInputExecutor battleInput,
    IRecognitionOverlayService overlay,
    ILogger<ManualChallengeRunner> logger) : IManualChallengeRunner
{
    private static readonly TimeSpan MyTurnCheckInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>中间跳过的黄框阶段超时：黄框（轮到玩家）出现后等 AI 插入超过 5 秒即强制执行。</summary>
    private const double SkipTurnTimeoutSeconds = 5;
    /// <summary>开头跳过的最短等待：从跳过开始至少满 10 秒且箭头顺延才执行后续操作。</summary>
    private const double HeadSkipMinimumSeconds = 10;

    public async Task RunLegendaryAsync(RuntimeTaskState state, string targetName, int runCount,
        int difficulty, bool useLegendaryMark, bool atTargetLocation, AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress, CancellationToken cancellationToken)
        => _ = await RunCoreAsync(state, null, runCount, battleSettings, progress, cancellationToken,
            targetName, useLegendaryMark, difficulty, atTargetLocation: atTargetLocation);

    public async Task RunAsync(RuntimeTaskState state, FlowerSeedOption target, int runCount, bool atTargetLocation,
        bool preferCondensedMark, AutoBattleSettings battleSettings, Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => _ = await RunCoreAsync(state, target, runCount, battleSettings, progress, cancellationToken,
            atTargetLocation: atTargetLocation, preferCondensedMark: preferCondensedMark);

    public Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => RunCoreAsync(state, null, 0, state.Options.AutoBattleSettings, progress, cancellationToken);

    public async Task<IReadOnlyList<string>> ScanLegendaryAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => (await RunCoreAsync(state, null, 0, state.Options.AutoBattleSettings, progress, cancellationToken, legendary: true))
            .Select(option => option.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();

    private async Task<IReadOnlyList<FlowerSeedOption>> RunCoreAsync(RuntimeTaskState state, FlowerSeedOption? target,
        int runCount, AutoBattleSettings battleSettings,
        Action<IndependentTaskProgress> progress, CancellationToken token, string? legendaryTarget = null, bool useLegendaryMark = true,
        int legendaryDifficulty = IndependentTaskSettings.DefaultLegendaryDifficulty, bool legendary = false,
        bool atTargetLocation = false, bool preferCondensedMark = false)
    {
        legendary |= legendaryTarget is not null;
        var scanning = target is null && legendaryTarget is null;
        var flow = new ManualChallengeFlow(target, runCount, legendaryTarget, useLegendaryMark, legendaryDifficulty, atTargetLocation);
        var settings = AutoBattleSettingsRules.Normalize(battleSettings);
        if (!scanning)
        {
            settings.IsEnabled = true;
            settings.ReleaseSequence = legendary ? settings.LegendaryReleaseSequence : settings.FlowerSeedReleaseSequence;
            settings.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        }
        var battle = new AutoBattleController();
        var loggedSkipTurnWaiting = false;
        var myTurnCache = false;
        var myTurnCheckedAt = DateTimeOffset.MinValue;
        DateTimeOffset? skipTurnHighlightAt = null;
        byte[]? skillQueueBaseline = null;
        var hasReleasedSkillInBattle = false;
        DateTimeOffset? headSkipStartedAt = null;
        var markClickAt = DateTimeOffset.MinValue;
        var useContractMark = false;
        var battleNumber = 0;
        var window = state.TargetWindow;
        var inputMethod = settings.KeyboardInputMethod;
        keyboard.EnsureReady(inputMethod);
        var lastScene = ManualChallengeScene.Unknown;
        var creatureName = legendaryTarget ?? target?.Name ?? string.Empty;
        var lastProgress = new IndependentTaskProgress("准备启动");
        var lastAdvance = DateTimeOffset.UtcNow;
        var lastAction = DateTimeOffset.MinValue;
        var lastActionScene = ManualChallengeScene.Unknown;
        var lastFrameTime = DateTimeOffset.MinValue;
        var minimumFrameAt = DateTimeOffset.UtcNow;
        CapturedFrame? pendingFrame = null;
        logger.LogInformation("手册挑战启动：{Target}，输入方式：{Method}", legendaryTarget ?? target?.DisplayName
            ?? (legendary ? "扫描传说精灵" : "扫描全部花种"), inputMethod);

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
                if (flow.Options.Count > count) lastAdvance = DateTimeOffset.UtcNow;
                if (screen.Scene == ManualChallengeScene.Battle && flow.IsBattleActive
                    || flow.CompletedCount != completedCount || flow.ConsecutiveFailures != failures)
                    lastAdvance = DateTimeOffset.UtcNow;
                if (flow.CompletedCount > completedCount)
                    logger.LogInformation("挑战成功，已完成 {Completed}/{Total} 次", flow.CompletedCount, runCount);
                if (flow.ConsecutiveFailures > failures)
                    logger.LogWarning("挑战失败，原地重试：连续失败 {Failures}/3，已成功 {Completed}/{Total} 次，触发画面 {Scene}",
                        flow.ConsecutiveFailures, flow.CompletedCount, runCount, screen.Scene);
                if (flow.BattleNumber != battleNumber)
                {
                    battleNumber = flow.BattleNumber;
                    battle.ResetBattle();
                    useContractMark = false;
                    markClickAt = DateTimeOffset.MinValue;
                    myTurnCache = false;
                    myTurnCheckedAt = DateTimeOffset.MinValue;
                    loggedSkipTurnWaiting = false;
                    skipTurnHighlightAt = null;
                    skillQueueBaseline = null;
                    hasReleasedSkillInBattle = false;
                    headSkipStartedAt = null;
                }
                AutoBattlePlan? plan = null;
                var switchingPet = decision.Action == ManualChallengeAction.Battle
                    && screen.BattleScreen is BattleScreen.PetSwitching or BattleScreen.PetSwitchConfirmation;
                if (decision.Action == ManualChallengeAction.Battle && screen.BattleScreen == BattleScreen.SkillSelection)
                {
                    var now = DateTimeOffset.UtcNow;
                    // 跳过回合步骤。序列开头：进战斗箭头先指第 1 个图标（本玩家可
                    // 出招，跳过即不出招），AI 插队出招后箭头顺延到第 2 个图标，此时
                    // 再执行后续技能。序列中间：等待第 1 个技能图标发生变化（出招队
                    // 列前移、释放者标注更换，说明 AI 的执行插入了），3 秒等不到就
                    // 直接执行（无人可插队时游戏要求必须出招）。
                    var waitingForMyTurn = false;
                    while (AutoBattleController.IsSkipTurnStep(battle.PeekReleaseStep(settings)))
                    {
                        // 开头跳过只在每场战斗的第一轮生效（未释放过任何技能）：
                        // 序列循环回第一步时箭头可能仍停在第 2 位（上一轮顺延出招后的
                        // 延续状态），若继续用"箭头顺延"判定会立即误通过。后续轮次
                        // 的跳过与序列中间的跳过一样，走技能界面 + 图标变化逻辑。
                        if (battle.RoundIndex > 0 || hasReleasedSkillInBattle)
                        {
                            // 中间跳过：进入技能选择界面即"轮到玩家"（本分支的前提
                            // 就是 BattleScreen.SkillSelection）。玩家不出招，等第 1
                            // 技能图标变化（AI 插入执行）；可操作状态累计 5 秒无人
                            // 插入才超时强制执行。AI 动画期间界面离开本分支，
                            // 计时重置、基线保留——回来时若图标已变即可完成。
                            skipTurnHighlightAt ??= DateTimeOffset.UtcNow;
                            if (skillQueueBaseline is null)
                            {
                                skillQueueBaseline = battleScreen.CaptureSkillQueueAnchor(state, frame, token);
                                if (!loggedSkipTurnWaiting)
                                {
                                    loggedSkipTurnWaiting = true;
                                    logger.LogInformation("手册挑战：已轮到玩家（技能选择界面），跳过本回合不出招，等待 AI 插入执行");
                                }
                            }
                            else if (battleScreen.HasSkillQueueChanged(state, frame, skillQueueBaseline, token))
                            {
                                battle.SkipReleaseStep();
                                skipTurnHighlightAt = null;
                                skillQueueBaseline = null;
                                myTurnCache = false;
                                loggedSkipTurnWaiting = false;
                                logger.LogInformation(
                                    "手册挑战：技能图标已变化（AI 执行插入），跳过回合步骤完成，继续执行 {Step}",
                                    AutoBattleSettingsRules.GetReleaseStepDisplay(battle.PeekReleaseStep(settings)));
                                continue;
                            }
                            else if ((DateTimeOffset.UtcNow - skipTurnHighlightAt.Value).TotalSeconds >= SkipTurnTimeoutSeconds)
                            {
                                battle.SkipReleaseStep();
                                skipTurnHighlightAt = null;
                                skillQueueBaseline = null;
                                myTurnCache = false;
                                loggedSkipTurnWaiting = false;
                                logger.LogInformation(
                                    "手册挑战：可操作状态等待超过 {Seconds} 秒仍无人插入，跳过回合超时兜底，继续执行 {Step}",
                                    SkipTurnTimeoutSeconds,
                                    AutoBattleSettingsRules.GetReleaseStepDisplay(battle.PeekReleaseStep(settings)));
                                continue;
                            }

                            waitingForMyTurn = true;
                            break;
                        }

                        var myTurn = myTurnCache;
                        if (!myTurn)
                        {
                            var check = DateTimeOffset.UtcNow;
                            if (check - myTurnCheckedAt >= MyTurnCheckInterval)
                            {
                                myTurnCheckedAt = check;
                                myTurn = myTurnCache = await battleScreen.IsMyTurnAsync(
                                    state, frame, settings.PlayerNumber, token);
                            }
                        }

                        // 开头跳过最短等待：从跳过开始计时，至少满 10 秒且箭头顺延才执行，
                        // 避免 boss/AI 刚插入（动画进行中）就出招。
                        headSkipStartedAt ??= DateTimeOffset.UtcNow;

                        if (!myTurn && now - headSkipStartedAt.Value >= TimeSpan.FromSeconds(30))
                            throw new TimeoutException("首步跳过等待超时：30 秒内未识别到顺延箭头。");

                        if (!myTurn || (DateTimeOffset.UtcNow - headSkipStartedAt.Value).TotalSeconds < HeadSkipMinimumSeconds)
                        {
                            waitingForMyTurn = true;
                            if (!loggedSkipTurnWaiting)
                            {
                                loggedSkipTurnWaiting = true;
                                logger.LogInformation(
                                    myTurn
                                        ? "手册挑战：箭头已顺延，但开头跳过最短等待 {Seconds} 秒未满，继续等待"
                                        : "手册挑战：跳过回合，等待 AI 出招后箭头顺延（当前未轮到顺延位），期间不发送输入",
                                    HeadSkipMinimumSeconds);
                            }

                            break;
                        }

                        battle.SkipReleaseStep();
                        skipTurnHighlightAt = null;
                        headSkipStartedAt = null;
                        myTurnCache = false;
                        loggedSkipTurnWaiting = false;
                        logger.LogInformation(
                            "手册挑战：箭头已顺延且最短等待 {Seconds} 秒已满，跳过回合步骤完成，继续执行 {Step}",
                            HeadSkipMinimumSeconds,
                            AutoBattleSettingsRules.GetReleaseStepDisplay(battle.PeekReleaseStep(settings)));
                    }

                    if (!waitingForMyTurn)
                    {
                        if (battle.Phase != AutoBattlePhase.SkillSelection)
                            battle.BeginSkillSelection(settings, now);
                        if (battle.ShouldRecoverAfterSkillFailure(settings, now))
                            plan = new(AutoBattleAction.EnergyRecovery, "X", "技能未离开选择界面，回能 X，原技能延后", "X");
                        else if (battle.CanAct(settings, now))
                            plan = battle.PlanSkillSelection(settings, false, now);
                    }
                }
                else if (screen.Scene != ManualChallengeScene.Unknown)
                {
                    battle.CompleteSkillSelection();
                    // 中间跳过等待期间界面离开（AI 行动动画）：不可操作状态，重置
                    // 5 秒计时；基线保留，界面回来时图标若已变化即可完成跳过。
                    skipTurnHighlightAt = null;
                }
                if (switchingPet && battle.Phase != AutoBattlePhase.PetSwitching)
                    battle.BeginPetSwitching(settings);
                else if (screen.Scene != ManualChallengeScene.Unknown && !switchingPet)
                    battle.ObservePetSwitching(false);
                // 捕捉前优先使用凝练印记：紫星未选中即点击切换（不做数量判断，
                // OCR 小数字不可靠）；出现"凝练印记不足"提示（数量耗尽）时改点
                // 蓝星（契约印记）。点击后本帧不捕捉，等下一帧复查选中状态。
                if (decision.Action == ManualChallengeAction.Capture
                    && preferCondensedMark && !legendary)
                {
                    var (markSelected, markInsufficient) = await recognizer.ReadCondensedMarkAsync(
                        frame, clientX, clientY, width, height, token);
                    useContractMark |= markInsufficient;
                    if (markInsufficient || markSelected == useContractMark)
                    {
                        decision = new(ManualChallengeAction.None);
                        if (DateTimeOffset.UtcNow - markClickAt >= TimeSpan.FromSeconds(2)
                            && keyboard.IsWindowForeground(window.Hwnd))
                        {
                            var point = RecognitionRegionImageHelper.MapReferenceArea(0, 0,
                                window.ClientWidth > 0 ? window.ClientWidth : width,
                                window.ClientHeight > 0 ? window.ClientHeight : height,
                                2048, 1152, useContractMark ? 1831 : 1962, 143, 1, 1);
                            await mouse.ClickAsync(window.Hwnd, point.X, point.Y, inputMethod, token);
                            markClickAt = minimumFrameAt = DateTimeOffset.UtcNow;
                        }
                    }
                }

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
                    ManualChallengeAction.None when screen.Scene == ManualChallengeScene.UnderfilledTeam => "等待识别助战确认按钮",
                    ManualChallengeAction.None when screen.Scene == ManualChallengeScene.CaptureConfirmation
                        && screen.Marks is null => "等待识别捕捉方式",
                    ManualChallengeAction.None => lastProgress.Operation,
                    ManualChallengeAction.Complete when scanning && legendary => "传说精灵扫描完成",
                    ManualChallengeAction.Complete when scanning => $"扫描完成，共{flow.Options.Count}个花种",
                    ManualChallengeAction.Complete => $"挑战完成，成功{flow.CompletedCount}次，已返回大世界",
                    ManualChallengeAction.Battle when switchingPet => "切换精灵",
                    ManualChallengeAction.Battle => plan is not null ? InfoOverlayIslandPresentation.BattleInputTitle(plan)
                        : screen.BattleScreen != BattleScreen.SkillSelection ? "等待行动"
                        : battle.CurrentTurn?.Action == AutoBattleAction.EnergyRecovery ? "等待重试" : "准备技能",
                    ManualChallengeAction.Capture => "捕捉",
                    ManualChallengeAction.Click when screen.Scene == ManualChallengeScene.LegendaryConfirmation
                        && screen.Difficulty != legendaryDifficulty => $"选择难度 {new string('I', legendaryDifficulty)}",
                    _ => Operation(decision.Action, screen.Scene)
                };
                Publish(lastProgress with { Operation = operation,
                    IsBattleWaiting = flow.IsBattleActive && plan is null && !switchingPet
                        && screen.Scene is ManualChallengeScene.Battle or ManualChallengeScene.Unknown,
                    Recognition = scanning ? recognized
                    : screen.Scene is ManualChallengeScene.Battle or ManualChallengeScene.Capture or ManualChallengeScene.Result
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
                            case ManualChallengeAction.ScrollBalls:
                                // 捕捉界面左侧球列表未显示专属球：鼠标移到球列表，
                                // 滚轮向上 4 下切出专属球。
                                await mouse.ScrollAsync(window.Hwnd,
                                    (int)Math.Round(nativeWidth * .19),
                                    (int)Math.Round(nativeHeight * .56),
                                    480, inputMethod, token);
                                logger.LogInformation("手册挑战：捕捉界面未显示专属球，已在球列表滚动切换");
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
                                            if (completed) logger.LogInformation("手册挑战：第 {Battle} 场第 {Turn} 回合，{Action}，已发送 {Sequence}",
                                                battleNumber, battle.TurnNumber, plan!.Description, sequence);
                                        }))
                                    break;
                                if (decision.Action == ManualChallengeAction.Battle && battle.CurrentTurn is { } currentTurn)
                                    battle.RecordAction(currentTurn.Id, plan!.Action, DateTimeOffset.UtcNow);
                                hasReleasedSkillInBattle = true;
                                logger.LogInformation("手册挑战：第 {Battle} 场，{Action}，序列 {Sequence}",
                                    battleNumber, plan!.Description, plan.Sequence);
                                if (decision.Action == ManualChallengeAction.Capture)
                                    await CloseCaptureRewardsAsync();
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

        async Task CloseCaptureRewardsAsync()
        {
            minimumFrameAt = DateTimeOffset.UtcNow;
            Publish(lastProgress with { Operation = "等待捕捉结算", IsBattleWaiting = false });
            var watchForShortage = preferCondensedMark && !legendary && !useContractMark;
            if (!watchForShortage) await Task.Delay(3000, token);
            // 结算演出可达 15 秒：不传 10 秒计时，由外层 90 秒兜底；
            // 印记不足提示只在捕捉后立刻出现，限定 3 秒窗口避免演出横幅误判提前返回。
            var shortageWindow = Stopwatch.StartNew();
            var total = Stopwatch.StartNew();
            while (true)
            {
                using var next = await CaptureNextFrameAsync(null, "关闭捕捉奖励");
                _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(next, window,
                    out var x, out var y, out var width, out var height);
                if (await recognizer.ReadResultAsync(next, x, y, width, height, token, legendary) is not null)
                {
                    pendingFrame = next.AddReference();
                    lastAdvance = DateTimeOffset.UtcNow;
                    return;
                }
                if (watchForShortage && shortageWindow.Elapsed < TimeSpan.FromSeconds(3))
                {
                    var mark = await recognizer.ReadCondensedMarkAsync(next, x, y, width, height, token);
                    if (mark.Insufficient)
                    {
                        useContractMark = true;
                        pendingFrame = next.AddReference();
                        return;
                    }
                    await Task.Delay(180, token);
                    continue;
                }
                // 识别到弹窗才点击；25 秒后固定点击兜底。
                var rewards = await recognizer.FindRewardsHeaderAsync(next, x, y, width, height, token);
                if (rewards is not null || total.Elapsed >= TimeSpan.FromSeconds(25))
                {
                    if (keyboard.IsWindowForeground(window.Hwnd))
                    {
                        Publish(lastProgress with { Operation = "关闭捕捉奖励", IsBattleWaiting = false });
                        var nativeWidth = window.ClientWidth > 0 ? window.ClientWidth : width;
                        var nativeHeight = window.ClientHeight > 0 ? window.ClientHeight : height;
                        await mouse.ClickAsync(window.Hwnd, (int)Math.Round(nativeWidth * .5),
                            (int)Math.Round(nativeHeight * .9), inputMethod, token);
                        minimumFrameAt = DateTimeOffset.UtcNow;
                    }
                }
                await Task.Delay(700, token);
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
            var operation = flow.StopReason is not null ? "退出并停止：" + flow.StopReason
                : scene == ManualChallengeScene.LegendaryConfirmation
                    && Path.GetFileNameWithoutExtension(initial.TemplatePath).StartsWith("difficulty-", StringComparison.Ordinal)
                    ? $"选择难度 {new string('I', legendaryDifficulty)}" : Operation(action, scene);
            var rowY = (initial.Y + initial.Height / 2d - clientY) / height;
            ImageMatchResult? button = initial;
            var attempts = 0;
            while (button is not null)
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"{operation}：输入 {attempts} 次后目标仍未消失（{initial.TemplatePath}）。");
                if (!button.IsMatch) Publish(lastProgress with { Operation = operation + (scene == ManualChallengeScene.UnderfilledTeam
                    ? "（等待确认弹窗状态）" : "（等待识别选中状态）") });
                // F3 是开关按键，只发一次；鼠标点击可以在原目标仍存在时重试。
                // 非空但未匹配表示动作结果尚不明确，只复查画面，不点击未确认的位置。
                if (button.IsMatch && keyboard.IsWindowForeground(window.Hwnd) && (action == ManualChallengeAction.Click || attempts == 0))
                {
                    Publish(lastProgress with { Operation = operation });
                    if (action == ManualChallengeAction.OpenManual)
                    {
                        await SendKey("F3", settings.KeyboardHoldDurationMs);
                        minimumFrameAt = DateTimeOffset.UtcNow;
                        attempts++;
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
                        attempts++;
                        logger.LogDebug("挑战点击：{Operation}，第 {Attempt} 次，模板 {Template}，得分 {Score:F3}，客户区坐标 ({X},{Y})",
                            operation, attempts, button.TemplatePath, button.Score, x, y);
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
        ManualChallengeScene.CaptureBallMissing => "捕捉界面（切换专属球）",
        ManualChallengeScene.Manual => "魔法师手册 - 学院作业",
        ManualChallengeScene.ChallengePage => "魔法师手册 - 挑战",
        ManualChallengeScene.FlowerList => "稀兽花种列表",
        ManualChallengeScene.Map => "挑战传送地图",
        ManualChallengeScene.Interaction => "挑战交互菜单",
        ManualChallengeScene.LegendaryList => "传说精灵列表",
        ManualChallengeScene.LegendaryConfirmation => "传说精灵挑战详情",
        ManualChallengeScene.UnderfilledTeam => "队伍不足，确认神秘帮手助战",
        ManualChallengeScene.InsufficientContractMark => "契约印记或本周捕捉次数不足提示",
        ManualChallengeScene.MissingLegendaryMark => "缺少专属印记提示",
        ManualChallengeScene.CaptureConfirmation => "选择捕捉印记",
        ManualChallengeScene.LegendaryRewards => "印记奖励",
        ManualChallengeScene.RewardSettlement => "获得奖励",
        ManualChallengeScene.Confirmation => "确认花种挑战",
        ManualChallengeScene.Preparation => "挑战准备",
        ManualChallengeScene.MedalConfirmation => "确认奖牌提示",
        ManualChallengeScene.Battle => "战斗",
        ManualChallengeScene.SkippableAnimation => "可跳过动画",
        ManualChallengeScene.Capture => "捕捉",
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
        ManualChallengeAction.Escape => scene switch
        {
            ManualChallengeScene.SkippableAnimation => "按Esc跳过动画",
            ManualChallengeScene.LegendaryRewards => "按Esc关闭奖励",
            _ => "按Esc退出挑战"
        },
        ManualChallengeAction.Click => scene switch
        {
            ManualChallengeScene.Manual => "打开挑战页面",
            ManualChallengeScene.ChallengePage => "打开目标挑战列表",
            ManualChallengeScene.FlowerList => "传送到目标花种",
            ManualChallengeScene.LegendaryList => "传送到目标传说精灵",
            ManualChallengeScene.LegendaryConfirmation => "点击开始挑战",
            ManualChallengeScene.UnderfilledTeam => "确认神秘帮手助战",
            ManualChallengeScene.InsufficientContractMark => "确认继续，使用传说印记捕捉",
            ManualChallengeScene.MissingLegendaryMark => "取消并退出挑战",
            ManualChallengeScene.CaptureConfirmation => "选择印记并确认捕捉",
            ManualChallengeScene.Map => "点击地图传送",
            ManualChallengeScene.Confirmation => "点击挑战",
            ManualChallengeScene.Preparation => "点击开始",
            ManualChallengeScene.MedalConfirmation => "确认奖牌提示，点击开始",
            ManualChallengeScene.Result => "选择再次挑战或退出",
            ManualChallengeScene.RewardSettlement => "点击关闭获得奖励",
            _ => "点击匹配按钮"
        },
        _ => "等待界面就绪"
    };
}
