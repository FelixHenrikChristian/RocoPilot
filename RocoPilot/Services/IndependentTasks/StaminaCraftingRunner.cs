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

    public async Task<string> RunAsync(RuntimeTaskState state, IndependentTaskSettings settings,
        Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var loop = new TaskLoop<Step>("体力合成", state, capture, keyboard, mouse, progress, Describe, Step.OpenMap, token, logger)
        {
            Timeout = _ => TimeSpan.FromSeconds(45)
        };
        var mark = settings.StaminaCraftingProduct == StaminaCraftingProductKind.CondensedMark;
        var outcome = mark ? "成功：已制造凝练印记" : "成功：已制造赛季球";
        var scrolls = 0;
        var nextResultEscape = ResultPause;

        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int left, int top, int right, int bottom, double threshold = .94) =>
                screen.Find($"stamina-crafting/{name}", left, top, right, bottom, token, threshold);
            Task<ImageMatchResult?> MakeButton() => Find("make", 1640, 1010, 1960, 1140);
            Task<ImageMatchResult?> BasicTitle() => Find("basic-title", 150, 0, 375, 115, .90);
            Task<ImageMatchResult?> Choices() => Find("choices", 1420, 620, 1950, 735, .90);
            Task<ImageMatchResult?> SeasonTimer() => Find("timer", 550, 870, 1020, 971);
            Task<ImageMatchResult?> Maximum() => Find("max", 1460, 975, 1560, 1060);

            Task<ImageMatchResult?> ConvertTitle() => Find("convert-title", 150, 0, 375, 115);
            async Task<bool> SelectedMark() =>
                await Find("mark-name", 430, 895, 590, 965, .85) is not null &&
                await Find("starseed", 290, 895, 440, 1025, .90) is not null;
            async Task<bool> RecipePanelVisible() =>
                await BasicTitle() is not null || (mark && await ConvertTitle() is not null);

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
                            if (await loop.KeyAsync("1")) loop.Advance(mark ? Step.ConvertTab : Step.SelectRecipe);
                        }
                        else if (await screen.Find("flower-seed/animation-skip", 80, 10, 240, 146, token) is not null)
                            await loop.RepeatKeyAsync("Esc");
                        else if (await Find("playing", 1700, 0, 1980, 102) is not null)
                            await loop.RepeatKeyAsync("Space");
                        break;
                    case Step.ConvertTab:
                        if (await ConvertTitle() is not null)
                        {
                            loop.Advance(Step.SelectMark);
                        }
                        else if (await BasicTitle() is not null)
                        {
                            var tab = screen.ReferenceToClient(173, 516);
                            if (await loop.ClickAsync(tab.X, tab.Y)) loop.Advance(Step.SelectMark);
                        }
                        break;
                    case Step.SelectMark:
                        if (await ConvertTitle() is null) break;
                        if (await SelectedMark())
                        {
                            loop.Advance(Step.SetAmount);
                            break;
                        }
                        if (await Find("mark-card", 250, 140, 1050, 870, .97) is { } card)
                        {
                            if (await loop.ClickAsync(screen, card)) loop.Advance(Step.SetAmount);
                            break;
                        }
                        if (await Find("mark-card-disabled", 250, 140, 1050, 870, .97) is not null)
                        {
                            outcome = "跳过：蜂蜜或体力不足";
                            loop.Advance(Step.CloseRecipe);
                        }
                        else if (loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                        {
                            if (scrolls >= 10)
                            {
                                outcome = "跳过：未找到凝练印记";
                                loop.Advance(Step.CloseRecipe);
                                break;
                            }
                            var list = screen.ReferenceToClient(650, 500);
                            if (await loop.ScrollAsync(list.X, list.Y, -480)) scrolls++;
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
                    case Step.Make:
                        if (mark ? !await SelectedMark() : await SeasonTimer() is null) break;
                        if (await Find("make-disabled", 1640, 1010, 1960, 1140) is not null)
                        {
                            outcome = mark ? "跳过：凝练印记不可制造" : "跳过：体力不足";
                            loop.Advance(Step.CloseRecipe);
                            break;
                        }
                        if (await Maximum() is not null)
                        {
                            if (await MakeButton() is { } make && await loop.ClickAsync(screen, make))
                                loop.Advance(Step.ConfirmMake);
                            break;
                        }
                        if (loop.Step == Step.SetAmount && await Find("handle", 1180, 970, 1305, 1070) is { } handle)
                        {
                            var px = handle.X + handle.Width / 2d;
                            var py = screen.ClientY(handle.Y + handle.Height / 2d);
                            // 略过滑轨右端再松手，避免数量差一档。
                            if (await loop.DragAsync(screen.ClientX(px), py, screen.ClientX(px + 340 * screen.Scale), py))
                                loop.Advance(Step.Make);
                        }
                        break;
                    case Step.ConfirmMake:
                        // 制造会消耗体力。按钮还在说明点击没生效，循环会回到上一步重新点击；消失后才开始等结算。
                        if (await MakeButton() is not null) break;
                        loop.CommittedOutcome = outcome;
                        nextResultEscape = ResultPause;
                        loop.Advance(Step.ExitRecipe);
                        break;
                    case Step.ExitRecipe:
                        if (loop.StepElapsed < nextResultEscape) break;
                        if (await RecipePanelVisible()) loop.Advance(Step.CloseRecipe);
                        else if (await loop.KeyAsync("Esc", react: false)) nextResultEscape = loop.StepElapsed + ResultPause;
                        break;
                    case Step.CloseRecipe:
                        if (await RecipePanelVisible() && await Find("basic-close", 1830, 12, 1960, 108) is { } close &&
                            await loop.ClickAsync(screen, close))
                            loop.Advance(Step.ExitDialogue);
                        break;
                    case Step.ExitDialogue:
                        if (await Choices() is not null)
                        {
                            if (await loop.RepeatKeyAsync("Esc")) loop.Advance(Step.ReturnWorld);
                        }
                        else if (await screen.World(token) is not null) return outcome;
                        else if (!await RecipePanelVisible()) await loop.EscapeUnknownAsync();
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

    private enum Step { OpenMap, SelectMap, Cauldron, Teleport, Approach, Dialogue, ConvertTab, SelectMark, SelectRecipe,
        SetAmount, Make, ConfirmMake, ExitRecipe, CloseRecipe, ExitDialogue, ReturnWorld }

    private static string Describe(Step step) => step switch
    {
        Step.OpenMap or Step.SelectMap => "选择家园种植园地图",
        Step.Cauldron or Step.Teleport => "传送到炼金釜",
        Step.Approach => "接近炼金釜",
        Step.Dialogue => "等待炼金造物选项",
        Step.ConvertTab => "切换到转化炼金",
        Step.SelectMark => "查找凝练印记",
        Step.SelectRecipe => "定位赛季球角标",
        Step.SetAmount => "确认配方并拖动制造次数",
        Step.Make => "确认最大次数和制造按钮",
        Step.ConfirmMake => "确认制造按钮消失",
        Step.ExitRecipe => "等待结算并返回炼金界面",
        _ => "退出炼金并返回大世界"
    };
}
