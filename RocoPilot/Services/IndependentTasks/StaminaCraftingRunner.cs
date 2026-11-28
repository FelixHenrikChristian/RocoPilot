using System.ComponentModel;

using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks.Engine;

namespace RocoPilot.Services.IndependentTasks;

public sealed class StaminaCraftingRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ILogger<StaminaCraftingRunner>? logger = null)
{
    // 结算动画期间 Esc 不起作用；制造按钮消失后及每次 Esc 之后都等这么久再按。
    private static readonly TimeSpan ResultPause = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConvertPanelWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MakeVerify = TimeSpan.FromSeconds(6);
    private const int MaxDialogueClicks = 8;

    // 左侧第 4 个类别（转化炼金）的参考坐标，两种合成页位置一致。
    private const double ConvertTabX = 173;
    private const double ConvertTabY = 516;

    // 凝练印记详情：紫色星星图标与名称同时命中才算选中，避免同色物品误判。
    private const int StarseedLeft = 250, StarseedTop = 860, StarseedRight = 445, StarseedBottom = 1040;
    private const double StarseedThreshold = .80;
    private const int MarkNameLeft = 430, MarkNameTop = 895, MarkNameRight = 590, MarkNameBottom = 965;
    private const double MarkNameThreshold = .85;

    public async Task<string> RunAsync(RuntimeTaskState state, IndependentTaskSettings settings,
        Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var condensed = settings.StaminaCraftingProduct == StaminaCraftingProductKind.CondensedMark;
        var loop = new TaskLoop<Step>("体力合成", state, capture, keyboard, mouse, progress, Describe, Step.OpenMap, token, logger)
        {
            Timeout = _ => TimeSpan.FromSeconds(45)
        };
        var outcome = condensed ? "成功：已制造凝练印记" : "成功：已制造赛季球";
        var nextResultEscape = ResultPause;
        var dialogueClicks = 0;
        var convertClicks = 0;

        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int left, int top, int right, int bottom, double threshold = .94) =>
                screen.Find($"stamina-crafting/{name}", left, top, right, bottom, token, threshold);
            Task<ImageMatchResult?> MakeButton() => Find("make", 1640, 1010, 1960, 1140);
            async Task<ImageMatchResult?> AnyMakeButton() =>
                await MakeButton() ?? await Find("make-disabled", 1640, 1010, 1960, 1140);
            Task<ImageMatchResult?> SliderHandle() => Find("handle", 1150, 950, 1450, 1090);
            async Task<bool> AlchemyPanel() => await AnyMakeButton() is not null || await SliderHandle() is not null;
            // 上游 1600x900 修复把 basic-title 阈值降到 0.90，这里保留。
            Task<ImageMatchResult?> BasicTitle() => Find("basic-title", 150, 0, 375, 115, .90);
            Task<ImageMatchResult?> Choices() => Find("choices", 1420, 620, 1950, 735, .90);
            Task<ImageMatchResult?> SeasonTimer() => Find("timer", 550, 870, 1020, 971);
            Task<ImageMatchResult?> Maximum() => Find("max", 1460, 975, 1560, 1060);
            Task<ImageMatchResult?> Starseed() =>
                Find("starseed", StarseedLeft, StarseedTop, StarseedRight, StarseedBottom, StarseedThreshold);
            Task<ImageMatchResult?> MarkName() =>
                Find("mark-name", MarkNameLeft, MarkNameTop, MarkNameRight, MarkNameBottom, MarkNameThreshold);
            async Task<bool> IsConcentratedMark() => await Starseed() is not null && await MarkName() is not null;

            // 气泡逐字出现时原地点击即可补全，不用把鼠标挪到固定位置；发不出去时下一轮重试。
            async Task ClickInPlaceAsync()
            {
                if (!loop.IsForeground) return;
                try
                {
                    await mouse.PressAtCurrentPositionAsync(window.Hwnd, 60, loop.Method, token);
                    loop.NoteInput();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
                {
                }
            }

            // 左侧第 4 个类别（转化炼金）：两种合成页位置一致，直接用参考坐标。
            async Task<bool> ClickConvertTabAsync()
            {
                var (clientX, clientY) = screen.ReferenceToClient(ConvertTabX, ConvertTabY);
                return await loop.ClickAsync(clientX, clientY);
            }

            async Task<bool> TryCloseAlchemyPanelAsync()
            {
                if (await Find("basic-close", 1600, 0, 2048, 260) is not { } close)
                    return false;
                return await loop.ClickAsync(screen, close);
            }

            var result = await loop.RunStepAsync(async () =>
            {
                switch (loop.Step)
                {
                    case Step.OpenMap:
                        if (await screen.Map(token) is not null) loop.Advance(Step.SelectMap);
                        else if (await screen.World(token) is not null && await loop.KeyAsync("M")) loop.Advance(Step.SelectMap);
                        break;
                    case Step.SelectMap:
                        var (selected, target) = await screen.SelectGarden(token);
                        if (selected)
                        {
                            if (target is null || await loop.ClickAsync(screen, target)) loop.Advance(Step.Cauldron);
                        }
                        // 展开地图列表的三角是开关，等列表展开后再判断，不连点。
                        else if (target is not null && loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                            await loop.ClickAsync(screen, target);
                        break;
                    case Step.Cauldron:
                        if (await screen.Garden(token) is not null &&
                            await Find("cauldron", 350, 210, 1530, 960) is { } marker &&
                            await loop.ClickAsync(screen, marker))
                            loop.Advance(Step.Teleport);
                        break;
                    case Step.Teleport:
                        if (await Find("teleport-title", 1280, 0, 1790, 118) is not null &&
                            await screen.Teleport(token) is { } teleport &&
                            await loop.ClickAsync(screen, teleport))
                            loop.Advance(Step.Approach);
                        break;
                    case Step.Approach:
                        if (await Find("interact", 1100, 420, 1610, 800) is not null)
                        {
                            if (await loop.KeyAsync("F")) loop.Advance(Step.Dialogue);
                        }
                        else if (await screen.World(token) is not null) await loop.KeyAsync("W", 300);
                        break;
                    case Step.Dialogue:
                        if (await Choices() is not null)
                        {
                            if (await loop.KeyAsync("1"))
                                // 炼金界面开启动画较慢，别再回头重放对话步骤，避免多按和多等一轮重试。
                                loop.Advance(condensed ? Step.OpenAlchemy : Step.SelectRecipe, retryable: !condensed);
                        }
                        else if (await screen.Find("flower-seed/animation-skip", 80, 10, 240, 146, token) is not null
                            || await Find("playing", 1700, 0, 1980, 102) is not null)
                        {
                            // Esc 只跳过过场动画；气泡仍在逐字出现时要点击补全，否则等不到选项。
                            if (await loop.RepeatKeyAsync("Esc") && dialogueClicks < MaxDialogueClicks)
                            {
                                dialogueClicks++;
                                await ClickInPlaceAsync();
                            }
                        }
                        else if (dialogueClicks > 0 && dialogueClicks < MaxDialogueClicks
                            && loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                        {
                            dialogueClicks++;
                            await ClickInPlaceAsync();
                        }
                        break;
                    case Step.OpenAlchemy:
                        if (await AlchemyPanel() && await ClickConvertTabAsync())
                        {
                            convertClicks = 1;
                            loop.Advance(Step.PickConvert);
                        }
                        break;
                    case Step.PickConvert:
                        if (await IsConcentratedMark())
                        {
                            loop.Advance(Step.SetAmount);
                        }
                        else if (convertClicks < 2)
                        {
                            // 类别切换偶尔吞掉第一次点击，补一次。
                            convertClicks++;
                            await ClickConvertTabAsync();
                        }
                        else if (loop.StepElapsed > ConvertPanelWait)
                        {
                            outcome = "跳过：炼金界面未选中凝练印记";
                            await loop.KeyAsync("Esc");
                            loop.Advance(Step.ExitDialogue);
                        }
                        break;
                    case Step.SelectRecipe:
                        // 1600×900 的标题匹配分数略低，点击仍须同时命中赛季球角标。
                        if (await Find("recipe", 40, 0, 370, 115, .90) is not null &&
                            await Find("badge", 250, 110, 1100, 906, .87) is { } badge &&
                            await loop.ClickAsync(screen, badge, -45, 45))
                            loop.Advance(Step.SetAmount);
                        break;
                    case Step.SetAmount:
                        // 凝练印记没有赛季图标，只有赛季球需要等它出现。
                        if (!condensed && await SeasonTimer() is null) break;
                        if (await Find("make-disabled", 1640, 1010, 1960, 1140) is not null)
                        {
                            outcome = "跳过：体力不足";
                            loop.Advance(Step.CloseRecipe);
                            break;
                        }
                        if (await Maximum() is not null)
                        {
                            if (await MakeButton() is { } maxButton && await loop.ClickAsync(screen, maxButton))
                                loop.Advance(Step.ConfirmMake);
                            break;
                        }
                        if (await Find("handle", 1180, 970, 1305, 1070) is { } handle)
                        {
                            var px = handle.X + handle.Width / 2d;
                            var py = screen.ClientY(handle.Y + handle.Height / 2d);
                            // 略过滑轨右端再松手，避免数量差一档。
                            if (await loop.DragAsync(screen.ClientX(px), py, screen.ClientX(px + 340 * screen.Scale), py))
                                loop.Advance(Step.Make);
                        }
                        break;
                    case Step.Make:
                        if (!condensed)
                        {
                            if (await SeasonTimer() is null) break;
                            if (await Find("make-disabled", 1640, 1010, 1960, 1140) is not null)
                            {
                                outcome = "跳过：体力不足";
                                loop.Advance(Step.CloseRecipe);
                                break;
                            }
                        }

                        // 凝练印记没有赛季图标，只等最大值标记；一直没出现说明拖拽没生效，跳过而不是空点。
                        if (await Maximum() is null)
                        {
                            if (condensed && loop.StepElapsed > MakeVerify)
                            {
                                outcome = "跳过：未能将制造次数拉满";
                                await loop.KeyAsync("Esc");
                                loop.Advance(Step.ExitDialogue);
                            }
                            break;
                        }

                        if (await AnyMakeButton() is { } makeButton && await loop.ClickAsync(screen, makeButton))
                            loop.Advance(Step.ConfirmMake);
                        break;
                    case Step.ConfirmMake:
                        if (await AnyMakeButton() is not null)
                        {
                            if (condensed && loop.StepElapsed > MakeVerify)
                            {
                                outcome = "跳过：材料不足，制造未生效";
                                await loop.KeyAsync("Esc");
                                loop.Advance(Step.ExitDialogue);
                            }
                            break;
                        }
                        loop.CommittedOutcome = outcome;
                        nextResultEscape = ResultPause;
                        loop.Advance(Step.ExitRecipe);
                        break;
                    case Step.ExitRecipe:
                        if (loop.StepElapsed < nextResultEscape) break;
                        if (condensed)
                        {
                            // 结算后先点右上角 X；面板已自动关闭或还在动画里就直接进入退出阶段。
                            if (await TryCloseAlchemyPanelAsync() || !await AlchemyPanel())
                                loop.Advance(Step.ExitDialogue);
                            else if (await loop.KeyAsync("Esc", react: false)) nextResultEscape = loop.StepElapsed + ResultPause;
                            break;
                        }
                        if (await BasicTitle() is not null) loop.Advance(Step.CloseRecipe);
                        else if (await loop.KeyAsync("Esc", react: false)) nextResultEscape = loop.StepElapsed + ResultPause;
                        break;
                    case Step.CloseRecipe:
                        if (await BasicTitle() is not null && await Find("basic-close", 1830, 12, 1960, 108) is { } close &&
                            await loop.ClickAsync(screen, close))
                            loop.Advance(Step.ExitDialogue);
                        break;
                    case Step.ExitDialogue:
                        if (await Choices() is not null)
                        {
                            if (await loop.RepeatKeyAsync("Esc")) loop.Advance(Step.ReturnWorld);
                        }
                        else if (await screen.World(token) is not null) return outcome;
                        else if (!condensed || !await TryCloseAlchemyPanelAsync())
                        {
                            if (await BasicTitle() is null) await loop.EscapeUnknownAsync();
                        }
                        break;
                    case Step.ReturnWorld:
                        if (await screen.World(token) is not null) return outcome;
                        if (await Choices() is not null) await loop.RepeatKeyAsync("Esc");
                        else await loop.EscapeUnknownAsync();
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }

    private enum Step
    {
        OpenMap, SelectMap, Cauldron, Teleport, Approach, Dialogue,
        OpenAlchemy, PickConvert, SelectRecipe, SetAmount, Make, ConfirmMake,
        ExitRecipe, CloseRecipe, ExitDialogue, ReturnWorld
    }

    private static string Describe(Step step) => step switch
    {
        Step.OpenMap or Step.SelectMap => "选择家园种植园地图",
        Step.Cauldron or Step.Teleport => "传送到炼金釜",
        Step.Approach => "接近炼金釜",
        Step.Dialogue => "等待炼金造物选项",
        Step.OpenAlchemy => "打开炼金界面",
        Step.PickConvert => "切换到转化炼金并确认凝练印记",
        Step.SelectRecipe => "定位赛季球角标",
        Step.SetAmount => "确认合成目标并拉满次数",
        Step.Make => "确认最大次数和制造按钮",
        Step.ConfirmMake => "确认制造按钮消失",
        Step.ExitRecipe => "等待结算并返回炼金界面",
        _ => "退出炼金并返回大世界"
    };
}
