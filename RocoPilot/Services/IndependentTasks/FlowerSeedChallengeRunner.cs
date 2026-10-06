using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Input;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.IndependentTasks;

public sealed class FlowerSeedChallengeRunner(
    IScreenCaptureService capture,
    IGameWindowService gameWindow,
    IKeyboardInputService keyboard,
    IMouseInputService mouse,
    FlowerSeedScreenRecognizer recognizer,
    IRecognitionOverlayService overlay,
    ILogger<FlowerSeedChallengeRunner> logger) : IFlowerSeedChallengeRunner
{
    public async Task RunAsync(RuntimeTaskState state, FlowerSeedOption target, Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => _ = await RunCoreAsync(state, target, progress, cancellationToken);

    public Task<IReadOnlyList<FlowerSeedOption>> ScanAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress,
        CancellationToken cancellationToken)
        => RunCoreAsync(state, null, progress, cancellationToken);

    private async Task<IReadOnlyList<FlowerSeedOption>> RunCoreAsync(RuntimeTaskState state, FlowerSeedOption? target,
        Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var flow = new FlowerSeedFlow(target);
        var window = state.TargetWindow;
        var inputMethod = state.Options.AutoBattleSettings.KeyboardInputMethod;
        keyboard.EnsureReady(inputMethod);
        var lastScene = FlowerSeedScene.Unknown;
        var lastProgress = new IndependentTaskProgress("准备启动");
        var lastAdvance = DateTimeOffset.UtcNow;
        var lastAction = DateTimeOffset.MinValue;
        var lastActionScene = FlowerSeedScene.Unknown;
        var lastFrameTime = DateTimeOffset.MinValue;
        var minimumFrameAt = DateTimeOffset.UtcNow;
        var foregroundAttempts = 0;
        logger.LogInformation("花种任务启动：{Target}，输入方式：{Method}", target?.DisplayName ?? "扫描全部花种", inputMethod);

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!keyboard.IsWindowAvailable(window.Hwnd))
                    throw new InvalidOperationException("游戏窗口已关闭。");
                if (!keyboard.IsWindowForeground(window.Hwnd))
                {
                    if (foregroundAttempts < 3)
                    {
                        foregroundAttempts++;
                        gameWindow.TryBringGameWindowToForeground(window);
                    }
                    Publish(lastProgress with { Operation = "等待游戏切换到前台" });
                    await Task.Delay(400, token);
                    lastAdvance = DateTimeOffset.UtcNow;
                    minimumFrameAt = lastAdvance;
                    continue;
                }
                foregroundAttempts = 0;
                if (DateTimeOffset.UtcNow - lastAdvance > TimeSpan.FromSeconds(90))
                    throw new TimeoutException($"等待画面匹配超时，最后确认步骤：{lastProgress.Stage}。");

                using var frame = await Task.Run(() => capture.Capture(window, state.Options.CaptureMethod), token);
                if (frame is null)
                {
                    Publish(lastProgress with { Recognition = "正在读取游戏画面" });
                    await Task.Delay(200, token);
                    continue;
                }
                if (frame.CapturedAt <= lastFrameTime || frame.CapturedAt < minimumFrameAt)
                {
                    await Task.Delay(120, token);
                    continue;
                }
                lastFrameTime = frame.CapturedAt;
                _ = RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window,
                    out var clientX, out var clientY, out var width, out var height);
                List<(RecognitionRegion Region, string Text)> previews = [];
                var screen = await recognizer.ReadAsync(frame, clientX, clientY, width, height, token,
                    state.Options.RecognitionOverlayEnabled ? (region, text) => previews.Add((region, text)) : null);
                if (screen.Scene != FlowerSeedScene.Unknown && screen.Scene != lastScene)
                {
                    lastScene = screen.Scene;
                    lastAdvance = DateTimeOffset.UtcNow;
                    lastProgress = new(Stage(screen.Scene));
                }
                var count = flow.Options.Count;
                var decision = flow.Next(screen, height);
                if (flow.Options.Count > count) lastAdvance = DateTimeOffset.UtcNow;
                if (screen.Scene == FlowerSeedScene.FlowerList)
                {
                    if (!flow.IsTopConfirmed) previews.Clear();
                    else for (var i = 0; i < previews.Count; i++)
                    {
                        previews[i].Region.Id = $"花种 {screen.Rows[i].Number}";
                        previews[i].Region.IsMatched = screen.Rows[i].HasMatched;
                    }
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
                    FlowerSeedScene.Unknown => "正在确认当前界面",
                    FlowerSeedScene.FlowerList when !flow.IsTopConfirmed => "正在定位列表顶部",
                    FlowerSeedScene.FlowerList => "当前花种：" + string.Join("、",
                        screen.Rows.Select(row => flow.Options[row.Number - 1].DisplayName)),
                    FlowerSeedScene.Map when target is not null => "当前花种：" +
                        (target with { Name = string.IsNullOrWhiteSpace(screen.Text) ? target.Name : screen.Text }).DisplayName,
                    FlowerSeedScene.Interaction => string.IsNullOrWhiteSpace(screen.Text)
                        ? "正在确认交互选项" : $"当前选项：{screen.Text}",
                    _ => $"当前界面：{Stage(screen.Scene)}"
                };
                var operation = decision.Action switch
                {
                    FlowerSeedAction.None => lastProgress.Operation,
                    FlowerSeedAction.Complete => target is null
                        ? $"扫描完成，共{flow.Options.Count}个花种" : "流程完成，已进入战斗",
                    _ => Operation(decision.Action, screen.Scene)
                };
                Publish(lastProgress with { Operation = operation, Recognition = recognized });
                if (decision.Action == FlowerSeedAction.Complete)
                    return flow.Options.ToArray();

                // 当前截图的结果先显示；输入改变画面后不能继续叠加这一帧的框。
                var previewEnabled = state.Options.RecognitionOverlayEnabled;
                if (previewEnabled) await Task.Delay(180, token);
                if (decision.Action != FlowerSeedAction.None && (screen.Scene != lastActionScene
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
                            case FlowerSeedAction.OpenManual:
                                await SendKey("F3", state.Options.AutoBattleSettings.KeyboardHoldDurationMs);
                                break;
                            case FlowerSeedAction.Click:
                                var button = decision.Button!;
                                await mouse.ClickAsync(window.Hwnd,
                                    (int)Math.Round((button.X + button.Width / 2d - clientX) * nativeWidth / width),
                                    (int)Math.Round((button.Y + button.Height / 2d - clientY) * nativeHeight / height), inputMethod, token);
                                break;
                            case FlowerSeedAction.ScrollUp:
                            case FlowerSeedAction.ScrollDown:
                                var row = screen.Rows[screen.Rows.Count / 2];
                                await mouse.ScrollAsync(window.Hwnd, (int)Math.Round(nativeWidth * .23),
                                    (int)Math.Round((row.Button.Y + row.Button.Height / 2d - clientY) * nativeHeight / height),
                                    decision.Action == FlowerSeedAction.ScrollUp ? 3600 : -360, inputMethod, token);
                                flow.RecordScroll(screen, height);
                                break;
                            case FlowerSeedAction.Approach:
                                await SendKey("W", 200);
                                break;
                            case FlowerSeedAction.SelectChallenge:
                                await mouse.ScrollAtCurrentPositionAsync(window.Hwnd, 120, inputMethod, token);
                                break;
                            case FlowerSeedAction.Interact:
                                await SendKey("F", state.Options.AutoBattleSettings.KeyboardHoldDurationMs);
                                break;
                        }
                        lastAction = DateTimeOffset.UtcNow;
                        lastActionScene = screen.Scene;
                        minimumFrameAt = lastAction;
                        // 滚轮动画结束后再比较行位置，避免把尚未更新的帧当作边界。
                        if (decision.Action is FlowerSeedAction.ScrollUp or FlowerSeedAction.ScrollDown)
                        {
                            minimumFrameAt = lastAction.AddMilliseconds(520);
                            await Task.Delay(520, token);
                        }
                    }
                    catch (InvalidOperationException) when (!keyboard.IsWindowForeground(window.Hwnd))
                    {
                        minimumFrameAt = DateTimeOffset.UtcNow;
                        Publish(lastProgress with { Operation = "等待游戏切换到前台" });
                    }
                }
                if (!previewEnabled) await Task.Delay(180, token);
            }
        }
        finally
        {
            overlay.Hide();
        }

        Task SendKey(string key, int holdDuration)
            => keyboard.SendSequenceAsync(window.Hwnd, key,
                new KeyboardInputOptions { Method = inputMethod, HoldDurationMs = holdDuration, IntervalMs = 0 }, token);

        void Publish(IndependentTaskProgress update)
        {
            if (update == lastProgress) return;
            lastProgress = update;
            progress(update);
        }
    }

    private static string Stage(FlowerSeedScene scene) => scene switch
    {
        FlowerSeedScene.World => "大世界",
        FlowerSeedScene.Manual => "魔法师手册 - 学院作业",
        FlowerSeedScene.ChallengePage => "魔法师手册 - 挑战",
        FlowerSeedScene.FlowerList => "稀兽花种列表",
        FlowerSeedScene.Map => "花种传送地图",
        FlowerSeedScene.Interaction => "花种交互菜单",
        FlowerSeedScene.Confirmation => "确认花种挑战",
        FlowerSeedScene.Preparation => "挑战准备",
        FlowerSeedScene.Battle => "战斗",
        _ => "等待画面匹配"
    };

    private static string Operation(FlowerSeedAction action, FlowerSeedScene scene) => action switch
    {
        FlowerSeedAction.OpenManual => "按F3打开手册",
        FlowerSeedAction.ScrollUp => "回到花种列表顶部",
        FlowerSeedAction.ScrollDown => "向下滚动花种列表",
        FlowerSeedAction.Approach => "按W靠近花种",
        FlowerSeedAction.SelectChallenge => "滚轮切换到挑战选项",
        FlowerSeedAction.Interact => "按F选择挑战",
        FlowerSeedAction.Click => scene switch
        {
            FlowerSeedScene.Manual => "打开挑战页面",
            FlowerSeedScene.ChallengePage => "打开稀兽花种列表",
            FlowerSeedScene.FlowerList => "传送到目标花种",
            FlowerSeedScene.Map => "点击地图传送",
            FlowerSeedScene.Confirmation => "点击挑战",
            FlowerSeedScene.Preparation => "点击开始",
            _ => "点击匹配按钮"
        },
        _ => "等待界面就绪"
    };
}
