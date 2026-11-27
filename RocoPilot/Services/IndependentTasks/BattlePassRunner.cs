using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.IndependentTasks.Engine;
using RocoPilot.Services.TextRecognition;
using Microsoft.Extensions.Logging;

namespace RocoPilot.Services.IndependentTasks;

public sealed class BattlePassRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, OnnxTextLocator locator,
    ILogger<BattlePassRunner>? logger = null)
{
    private enum Step { World, Menu, EnterTasks, Tasks, ClaimTasks, DismissTasks, OpenRewards, Rewards, ClaimRewards, DismissRewards, ExitRewards, ExitMenu, ExitWorld }

    internal static async Task<bool> IsTasksPageAsync(DailyTaskScreen screen, CancellationToken token) =>
        await screen.Find("battle-pass/tasks-tab", 125, 257, 209, 350, token) is not null;

    internal static async Task<bool> IsRewardsPageAsync(DailyTaskScreen screen, CancellationToken token) =>
        await screen.Find("battle-pass/rewards-tab", 124, 148, 210, 234, token) is not null;

    internal static async Task<bool> HasClaimAllAsync(DailyTaskScreen screen, CancellationToken token) =>
        await screen.Find("battle-pass/claim-all", 1410, 1040, 1658, 1126, token) is not null;

    internal static async Task<List<int>> FindTaskButtonsAsync(DailyTaskScreen screen, CancellationToken token)
    {
        var buttons = new List<int>(4);
        for (var i = 0; i < 4; i++)
        {
            var x = 805 + i * 320;
            if (await screen.Find("battle-pass/claim-task", x - 152, 783, x + 152, 870, token) is not null)
                buttons.Add(x);
        }
        return buttons;
    }

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var claimed = 0;
        var pendingButtons = 0;
        var stable = 0;
        string Describe(Step step) => step switch
        {
            Step.World or Step.Menu => "打开通行证",
            Step.EnterTasks or Step.Tasks => "检查四项日常任务",
            Step.ClaimTasks or Step.ClaimRewards => "确认领取界面切换",
            Step.DismissTasks or Step.DismissRewards => "关闭弹窗并核对领取结果",
            Step.OpenRewards or Step.Rewards => "领取通行证等级奖励",
            _ => $"通行证任务领取了{claimed}/4，返回大世界"
        };
        // 多处靠“连续两次读数一致”确认，识别间隔比引擎默认值放慢一些，让两次读数隔开。
        var loop = new TaskLoop<Step>("通行证奖励", state, capture, keyboard, mouse, progress, Describe, Step.World, token, logger)
        {
            Poll = _ => 250
        };
        void Go(Step next)
        {
            stable = 0;
            loop.Advance(next);
        }
        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<RecognitionRegion?> Menu() => locator.FindAsync(frame, window, "通行证", token);
            Task<bool> Claim() => HasClaimAllAsync(screen, token);
            Task<bool> Tasks() => IsTasksPageAsync(screen, token);
            Task<bool> ClickRegion(RecognitionRegion region) => loop.ClickAsync(
                screen.ClientX(region.X + region.Width / 2d), screen.ClientY(region.Y + region.Height / 2d));
            Task<bool> ClickPoint(int x, int y) => ClickRegion(screen.Region(x, y, x + 1, y + 1));

            var result = await loop.RunStepAsync(async () =>
            {
                switch (loop.Step)
                {
                    case Step.World:
                        if (await screen.World(token) is not null && await loop.KeyAsync("F1")) Go(Step.Menu);
                        break;
                    case Step.Menu:
                        if (await Menu() is { } entry && await ClickRegion(entry)) Go(Step.EnterTasks);
                        break;
                    case Step.EnterTasks:
                    case Step.Tasks:
                        if (!await Tasks())
                        {
                            stable = 0;
                            // 打开时停在等级奖励页：切到日常任务页，等页面切换后再判断。
                            if (loop.InputIdleFor(TaskPacing.RepeatKeyMs) && await IsRewardsPageAsync(screen, token))
                                await ClickPoint(166, 304);
                            break;
                        }
                        if (loop.Step == Step.EnterTasks)
                        {
                            // “全部领取”先于四张卡片出现，等入场动画结束后用下一帧计数。
                            await Task.Delay(TimeSpan.FromSeconds(1), token);
                            Go(Step.Tasks);
                            break;
                        }
                        var buttons = await FindTaskButtonsAsync(screen, token);
                        var claimAll = await Claim();
                        if (claimAll || buttons.Count > 0)
                        {
                            stable = 0;
                            if (await ClickPoint(claimAll ? 1535 : buttons[0], claimAll ? 1083 : 828))
                            {
                                pendingButtons = buttons.Count;
                                Go(Step.ClaimTasks);
                            }
                        }
                        else if (++stable >= 2) Go(Step.OpenRewards);
                        break;
                    case Step.ClaimTasks:
                        if (!await Tasks() || (await FindTaskButtonsAsync(screen, token)).Count < pendingButtons ||
                            (pendingButtons == 0 && !await Claim())) Go(Step.DismissTasks);
                        break;
                    case Step.ClaimRewards:
                        // 领取按钮仍在时不发送 ESC，也不把一次点击当作领取成功；循环会回到上一步重新点击。
                        if (!await Claim()) Go(Step.DismissRewards);
                        break;
                    case Step.DismissTasks:
                    case Step.DismissRewards:
                        var tasks = loop.Step == Step.DismissTasks;
                        var pageReady = tasks ? await Tasks() : await IsRewardsPageAsync(screen, token);
                        if (pageReady)
                        {
                            if (++stable < 2) break;
                            if (tasks)
                            {
                                var remaining = await FindTaskButtonsAsync(screen, token);
                                claimed += Math.Max(0, pendingButtons - remaining.Count);
                                Go(remaining.Count > 0 || await Claim() ? Step.Tasks : Step.OpenRewards);
                            }
                            else Go(await Claim() ? Step.Rewards : Step.ExitRewards);
                        }
                        else
                        {
                            stable = 0;
                            await loop.RepeatKeyAsync("Esc");
                        }
                        break;
                    case Step.OpenRewards:
                        if (await Tasks() && await ClickPoint(166, 192)) Go(Step.Rewards);
                        break;
                    case Step.Rewards:
                        if (!await IsRewardsPageAsync(screen, token)) { stable = 0; break; }
                        if (await Claim())
                        {
                            if (await ClickPoint(1535, 1083)) Go(Step.ClaimRewards);
                        }
                        else if (++stable >= 2) Go(Step.ExitRewards);
                        break;
                    case Step.ExitRewards:
                        if (await IsRewardsPageAsync(screen, token) && await loop.KeyAsync("Esc")) Go(Step.ExitMenu);
                        break;
                    case Step.ExitMenu:
                        if (await Menu() is not null && await loop.KeyAsync("Esc")) Go(Step.ExitWorld);
                        break;
                    case Step.ExitWorld:
                        if (await screen.World(token) is not null) return $"通行证任务领取了{claimed}/4";
                        // 菜单还在时由循环回到上一步重新按 Esc；这里只管认不出的画面（升级弹窗晚出现）。
                        if (await Menu() is null) await loop.EscapeUnknownAsync();
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }
}
