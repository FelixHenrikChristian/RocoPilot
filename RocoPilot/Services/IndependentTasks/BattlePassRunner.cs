using System.Text.RegularExpressions;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.IndependentTasks.Engine;
using RocoPilot.Services.TextRecognition;
using Microsoft.Extensions.Logging;

namespace RocoPilot.Services.IndependentTasks;

public sealed class BattlePassRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ITextRecognitionService ocr, OnnxTextLocator locator,
    ILogger<BattlePassRunner>? logger = null)
{
    private enum Step { World, Menu, Tasks, ClaimTasks, DismissTasks, OpenRewards, Rewards, ClaimRewards, DismissRewards, ExitRewards, ExitMenu, ExitWorld }

    internal static async Task<string> ReadAsync(CapturedFrame frame, DailyTaskScreen screen,
        ITextRecognitionService ocr, int left, int top, int right, int bottom, CancellationToken token) =>
        Regex.Replace((await ocr.RecognizeAsync(frame, screen.Region(left, top, right, bottom),
            TextRecognitionMethod.OnnxOcrV5, token)).Text, @"\s", "");

    // 四张卡片的进度必须同时可见，避免把弹窗后面露出的局部任务页当成返回成功。
    internal static async Task<int?> ReadCompletedAsync(CapturedFrame frame, DailyTaskScreen screen,
        ITextRecognitionService ocr, CancellationToken token)
    {
        var completed = 0;
        for (var i = 0; i < 4; i++)
        {
            // 短进度收窄左右留白；第三项为小字号长数字，保持单独的行高。
            var text = await ReadAsync(frame, screen, ocr, 678 + i * 320 + (i is 0 or 3 ? 66 : 0), i == 2 ? 475 : 463,
                932 + i * 320 - (i is 0 or 3 ? 66 : 0), i == 2 ? 511 : 520, token);
            var match = Regex.Match(text, @"^(\d+)/(\d+)$");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var current) ||
                !int.TryParse(match.Groups[2].Value, out var total) || total <= 0 || current > total) return null;
            if (current == total) completed++;
        }
        return completed;
    }

    internal static async Task<bool> HasClaimableAsync(CapturedFrame frame, DailyTaskScreen screen,
        ITextRecognitionService ocr, CancellationToken token)
    {
        for (var i = 0; i < 4; i++)
            if ((await ReadAsync(frame, screen, ocr, 708 + i * 320, 802, 905 + i * 320, 853, token))
                .Contains("领取")) return true;
        return false;
    }

    // 标题在奖励弹窗后仍然可见；同时核对中部礼券文字，确认已回到未遮挡的等级页。
    internal static async Task<bool> ReadRewardsPageAsync(CapturedFrame frame, DailyTaskScreen screen,
        ITextRecognitionService ocr, CancellationToken token) =>
        (await ReadAsync(frame, screen, ocr, 195, 57, 340, 96, token)) == "等级奖励" &&
        (await ReadAsync(frame, screen, ocr, 660, 514, 805, 553, token)) == "基础礼券";

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var completed = 0;
        var stable = 0;
        string Describe(Step step) => step switch
        {
            Step.World or Step.Menu => "打开通行证",
            Step.Tasks => "检查四项日常任务",
            Step.ClaimTasks or Step.ClaimRewards => "确认领取界面切换",
            Step.DismissTasks or Step.DismissRewards => "关闭弹窗并核对领取结果",
            Step.OpenRewards or Step.Rewards => "领取通行证等级奖励",
            _ => $"通行证任务领取了{completed}/4，返回大世界"
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
            Task<string> Read(int l, int t, int r, int b) => ReadAsync(frame, screen, ocr, l, t, r, b, token);
            Task<RecognitionRegion?> Menu() => locator.FindAsync(frame, window, "通行证", token);
            async Task<bool> Claim() => await Read(1460, 1063, 1608, 1106) == "全部领取";
            async Task<int?> Tasks() => (await Read(195, 57, 340, 96)).Contains("日常任务")
                ? await ReadCompletedAsync(frame, screen, ocr, token) : null;
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
                        if (await Menu() is { } entry && await ClickRegion(entry)) Go(Step.Tasks);
                        break;
                    case Step.Tasks:
                        if (await Tasks() is not { } count)
                        {
                            stable = 0;
                            // 打开时停在等级奖励页：切到日常任务页，等页面切换后再判断。
                            if (loop.InputIdleFor(TaskPacing.RepeatKeyMs) && await ReadRewardsPageAsync(frame, screen, ocr, token))
                                await ClickPoint(166, 304);
                            break;
                        }
                        completed = count;
                        if (await Claim())
                        {
                            if (await ClickPoint(1535, 1083)) Go(Step.ClaimTasks);
                        }
                        else if (await HasClaimableAsync(frame, screen, ocr, token)) stable = 0;
                        else if (++stable >= 2) Go(Step.OpenRewards);
                        break;
                    case Step.ClaimTasks:
                    case Step.ClaimRewards:
                        // 领取按钮仍在时不发送 ESC，也不把一次点击当作领取成功；循环会回到上一步重新点击。
                        if (!await Claim()) Go(loop.Step == Step.ClaimTasks ? Step.DismissTasks : Step.DismissRewards);
                        break;
                    case Step.DismissTasks:
                    case Step.DismissRewards:
                        var tasks = loop.Step == Step.DismissTasks;
                        var after = tasks ? await Tasks() : null;
                        var pageReady = tasks ? after.HasValue : await ReadRewardsPageAsync(frame, screen, ocr, token);
                        if (pageReady)
                        {
                            if (await Claim() || (tasks && (after != completed || await HasClaimableAsync(frame, screen, ocr, token))))
                            { stable = 0; break; }
                            if (++stable >= 2) Go(tasks ? Step.OpenRewards : Step.ExitRewards);
                        }
                        else
                        {
                            stable = 0;
                            await loop.RepeatKeyAsync("Esc");
                        }
                        break;
                    case Step.OpenRewards:
                        if (await Tasks() is not null && await ClickPoint(166, 192)) Go(Step.Rewards);
                        break;
                    case Step.Rewards:
                        if (!await ReadRewardsPageAsync(frame, screen, ocr, token)) { stable = 0; break; }
                        if (await Claim())
                        {
                            if (await ClickPoint(1535, 1083)) Go(Step.ClaimRewards);
                        }
                        else if (++stable >= 2) Go(Step.ExitRewards);
                        break;
                    case Step.ExitRewards:
                        if (await ReadRewardsPageAsync(frame, screen, ocr, token) && await loop.KeyAsync("Esc")) Go(Step.ExitMenu);
                        break;
                    case Step.ExitMenu:
                        if (await Menu() is not null && await loop.KeyAsync("Esc")) Go(Step.ExitWorld);
                        break;
                    case Step.ExitWorld:
                        if (await screen.World(token) is not null) return $"通行证任务领取了{completed}/4";
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }
}
