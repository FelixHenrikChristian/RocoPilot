using System.Diagnostics;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

public sealed class StaminaCraftingRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse)
{
    private const int FramePollMs = 350;
    private const int ActionPauseMs = 900;
    private static readonly TimeSpan ResultPause = TimeSpan.FromSeconds(3);

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        keyboard.EnsureReady(input.KeyboardInputMethod);
        var step = Step.OpenMap;
        var outcome = "成功：已制造赛季球";
        var timer = Stopwatch.StartNew();
        var nextResultEscape = ResultPause;
        DateTimeOffset? lastResultFrame = null;
        var freshAfter = DateTimeOffset.UtcNow;
        progress(new("体力合成", "打开地图"));

        while (true)
        {
            await Task.Delay(FramePollMs, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                if (timer.IsRunning) progress(new("体力合成", "已暂停，等待切回游戏"));
                timer.Stop();
                lastResultFrame = null;
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (!timer.IsRunning)
            {
                timer.Start();
                progress(new("体力合成", Describe(step)));
            }
            if (timer.Elapsed > TimeSpan.FromSeconds(45))
                throw new TimeoutException($"体力合成等待超时：{Describe(step)}。");

            using var frame = await Task.Run(() => capture.Capture(window, state.Options.CaptureMethod), token);
            // 静止的结算画面可能只返回上一帧；允许在同一帧上按间隔重试 ESC。
            if (frame is null || (frame.CapturedAt <= freshAfter &&
                !(step == Step.ExitRecipe && frame.CapturedAt == lastResultFrame && timer.Elapsed >= nextResultEscape))) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int left, int top, int right, int bottom, double threshold = .94) =>
                screen.Find($"stamina-crafting/{name}", left, top, right, bottom, token, threshold);
            Task<ImageMatchResult?> MakeButton() => Find("make", 1640, 1010, 1960, 1140);
            Task<ImageMatchResult?> Recipe() => Find("recipe", 40, 0, 370, 115);
            Task<ImageMatchResult?> Choices() => Find("choices", 1420, 620, 1950, 735, .90);
            Task<ImageMatchResult?> SeasonTimer() => Find("timer", 550, 870, 1020, 971);
            async Task<ImageMatchResult?> GardenOption() =>
                await Find("garden-option", 100, 300, 485, 1045, .90) ??
                await Find("garden-option-selected", 100, 300, 485, 1045, .90);
            Task<ImageMatchResult?> Maximum() => Find("max", 1460, 975, 1560, 1060);
            Task<ImageMatchResult?> Skip() => screen.Find("flower-seed/animation-skip", 80, 10, 240, 146, token);
            async Task AfterInput()
            {
                freshAfter = DateTimeOffset.UtcNow;
                await Task.Delay(ActionPauseMs, token);
            }

            async Task Click(ImageMatchResult target, int dx = 0, int dy = 0)
            {
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(target.X + target.Width / 2d + dx * screen.Scale),
                    screen.ClientY(target.Y + target.Height / 2d + dy * screen.Scale), input.KeyboardInputMethod, token);
                await AfterInput();
            }

            async Task Key(string key, int? hold = null)
            {
                if (!keyboard.IsWindowForeground(window.Hwnd))
                    throw new InvalidOperationException("游戏窗口已离开前台。");
                await keyboard.SendSequenceAsync(window.Hwnd, key,
                    new() { Method = input.KeyboardInputMethod, HoldDurationMs = hold ?? input.KeyboardHoldDurationMs }, token);
                await AfterInput();
            }

            var previous = step;
            switch (step)
            {
                case Step.OpenMap:
                    if (await Find("map", 45, 0, 320, 120) is not null) step = Step.SelectMap;
                    else if (await screen.World(token) is not null)
                    {
                        await Key("M");
                        step = Step.SelectMap;
                    }
                    break;
                case Step.SelectMap:
                    if (await Find("map", 45, 0, 320, 120) is null) break;
                    if (await GardenOption() is { } option)
                    {
                        await Click(option);
                        step = Step.Cauldron;
                    }
                    else if (await Find("garden", 185, 36, 405, 103) is not null)
                        step = Step.Cauldron;
                    else if (await Find("triangle", 180, 1010, 500, 1120) is { } arrow)
                    {
                        await Click(arrow);
                        step = Step.PickGarden;
                    }
                    break;
                case Step.PickGarden:
                    if (await GardenOption() is { } garden)
                    {
                        await Click(garden);
                        step = Step.Cauldron;
                    }
                    break;
                case Step.Cauldron:
                    if (await Find("garden", 185, 36, 405, 103) is not null &&
                        await Find("cauldron", 350, 210, 1530, 960) is { } marker)
                    {
                        await Click(marker);
                        step = Step.Teleport;
                    }
                    break;
                case Step.Teleport:
                    if (await Find("teleport-title", 1280, 0, 1790, 118) is not null &&
                        await screen.Teleport(token) is { } teleport)
                    {
                        await Click(teleport);
                        step = Step.Approach;
                    }
                    break;
                case Step.Approach:
                    if (await Find("interact", 1100, 420, 1610, 800) is not null)
                    {
                        await Key("F");
                        step = Step.Dialogue;
                    }
                    else if (await screen.World(token) is not null) await Key("W", 300);
                    break;
                case Step.Dialogue:
                    if (await Choices() is not null)
                    {
                        await Key("1");
                        step = Step.SelectRecipe;
                    }
                    else if (await Skip() is not null || await Find("playing", 1700, 0, 1980, 102) is not null)
                        await Key("Esc");
                    break;
                case Step.SelectRecipe:
                    if (await Recipe() is not null && await Find("badge", 250, 110, 1100, 906, .87) is { } badge)
                    {
                        await Click(badge, -45, 45);
                        step = Step.SetAmount;
                    }
                    break;
                case Step.SetAmount:
                case Step.Make:
                    if (await SeasonTimer() is null) break;
                    if (await Find("make-disabled", 1640, 1010, 1960, 1140) is not null)
                    {
                        outcome = "跳过：体力不足";
                        await Key("Esc");
                        step = Step.ExitDialogue;
                        break;
                    }
                    if (await Maximum() is not null)
                    {
                        if (await MakeButton() is { } make)
                        {
                            await Click(make);
                            step = Step.ConfirmMake;
                        }
                        break;
                    }
                    if (step == Step.SetAmount && await Find("handle", 1180, 970, 1305, 1070) is { } handle)
                    {
                        var px = handle.X + handle.Width / 2d;
                        var py = screen.ClientY(handle.Y + handle.Height / 2d);
                        await mouse.DragAsync(window.Hwnd, screen.ClientX(px), py, screen.ClientX(px + 276 * screen.Scale), py,
                            input.KeyboardInputMethod, token);
                        await AfterInput();
                        step = Step.Make;
                    }
                    break;
                case Step.ConfirmMake:
                    // 制造会消耗体力。复查同一按钮，确认消失后再开始计时。
                    if (await MakeButton() is null) step = Step.ExitRecipe;
                    break;
                case Step.ExitRecipe:
                    if (timer.Elapsed < nextResultEscape) break;
                    var returnedToRecipe = await Recipe() is not null;
                    lastResultFrame = frame.CapturedAt;
                    await Key("Esc");
                    if (returnedToRecipe) step = Step.ExitDialogue;
                    else nextResultEscape = timer.Elapsed + ResultPause;
                    break;
                case Step.ExitDialogue:
                    if (await Choices() is not null)
                    {
                        await Key("Esc");
                        step = Step.ReturnWorld;
                    }
                    else if (await Recipe() is not null) await Key("Esc");
                    break;
                case Step.ReturnWorld:
                    if (await screen.World(token) is not null) return outcome;
                    if (await Choices() is not null) await Key("Esc");
                    break;
            }

            if (previous != step)
            {
                timer.Restart();
                progress(new("体力合成", Describe(step)));
            }
        }
    }

    private enum Step { OpenMap, SelectMap, PickGarden, Cauldron, Teleport, Approach, Dialogue, SelectRecipe,
        SetAmount, Make, ConfirmMake, ExitRecipe, ExitDialogue, ReturnWorld }

    private static string Describe(Step step) => step switch
    {
        Step.OpenMap or Step.SelectMap or Step.PickGarden => "选择家园种植园地图",
        Step.Cauldron or Step.Teleport => "传送到炼金釜",
        Step.Approach => "接近炼金釜",
        Step.Dialogue => "进入炼金造物",
        Step.SelectRecipe => "定位赛季球角标",
        Step.SetAmount => "确认赛季球并拖动制造次数",
        Step.Make => "确认最大次数和制造按钮",
        Step.ConfirmMake => "确认制造按钮消失",
        Step.ExitRecipe => "等待结算并返回炼金界面",
        _ => "退出炼金并返回大世界"
    };
}
