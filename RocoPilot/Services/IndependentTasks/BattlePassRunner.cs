using System.Diagnostics;
using System.Text.RegularExpressions;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.TextRecognition;

namespace RocoPilot.Services.IndependentTasks;

public sealed class BattlePassRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ITextRecognitionService ocr, OnnxTextLocator locator)
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
        var input = state.Options.AutoBattleSettings;
        keyboard.EnsureReady(input.KeyboardInputMethod);
        var step = Step.World;
        var timer = Stopwatch.StartNew();
        var freshAfter = DateTimeOffset.MinValue;
        var completed = 0;
        var stable = 0;
        string Status() => step switch
        {
            Step.World or Step.Menu => "打开通行证",
            Step.Tasks => "检查四项日常任务",
            Step.ClaimTasks or Step.ClaimRewards => "确认领取界面切换",
            Step.DismissTasks or Step.DismissRewards => "关闭弹窗并核对领取结果",
            Step.OpenRewards or Step.Rewards => "领取通行证等级奖励",
            _ => $"通行证任务领取了{completed}/4，返回大世界"
        };
        progress(new("通行证奖励", Status()));
        while (true)
        {
            await Task.Delay(350, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                if (timer.IsRunning) progress(new("通行证奖励", "已暂停，等待切回游戏"));
                timer.Stop();
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (!timer.IsRunning) { timer.Start(); progress(new("通行证奖励", Status())); }
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException($"通行证奖励等待超时：{Status()}。");
            using var frame = capture.Capture(window, state.Options.CaptureMethod);
            if (frame is null || frame.CapturedAt <= freshAfter) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<string> Read(int l, int t, int r, int b) => ReadAsync(frame, screen, ocr, l, t, r, b, token);
            Task<RecognitionRegion?> Menu() => locator.FindAsync(frame, window, "通行证", token);
            async Task<bool> Claim() => await Read(1460, 1063, 1608, 1106) == "全部领取";
            async Task<int?> Tasks() => (await Read(195, 57, 340, 96)).Contains("日常任务")
                ? await ReadCompletedAsync(frame, screen, ocr, token) : null;
            var next = step;
            string? key = null;
            RecognitionRegion? click = null;
            switch (step)
            {
                case Step.World:
                    if (await screen.World(token) is null) break;
                    key = "F1"; next = Step.Menu; break;
                case Step.Menu:
                    click = await Menu();
                    if (click is null) break;
                    next = Step.Tasks; break;
                case Step.Tasks:
                    if (await Tasks() is not { } count)
                    {
                        stable = 0;
                        if (await ReadRewardsPageAsync(frame, screen, ocr, token))
                            click = screen.Region(166, 304, 167, 305);
                        break;
                    }
                    completed = count;
                    if (await Claim()) { click = screen.Region(1535, 1083, 1536, 1084); next = Step.ClaimTasks; }
                    else if (await HasClaimableAsync(frame, screen, ocr, token)) stable = 0;
                    else if (++stable >= 2) next = Step.OpenRewards;
                    break;
                case Step.ClaimTasks:
                case Step.ClaimRewards:
                    // 领取按钮仍在时不发送 ESC，也不把一次点击当作领取成功。
                    if (!await Claim()) next = step == Step.ClaimTasks ? Step.DismissTasks : Step.DismissRewards;
                    break;
                case Step.DismissTasks:
                case Step.DismissRewards:
                    var tasks = step == Step.DismissTasks;
                    var after = tasks ? await Tasks() : null;
                    var pageReady = tasks ? after.HasValue : await ReadRewardsPageAsync(frame, screen, ocr, token);
                    if (pageReady)
                    {
                        if (await Claim() || (tasks && (after != completed || await HasClaimableAsync(frame, screen, ocr, token))))
                        { stable = 0; break; }
                        if (++stable >= 2) next = tasks ? Step.OpenRewards : Step.ExitRewards;
                    }
                    else { stable = 0; key = "Esc"; }
                    break;
                case Step.OpenRewards:
                    if (await Tasks() is null) break;
                    click = screen.Region(166, 192, 167, 193);
                    next = Step.Rewards;
                    break;
                case Step.Rewards:
                    if (!await ReadRewardsPageAsync(frame, screen, ocr, token)) { stable = 0; break; }
                    if (await Claim()) { click = screen.Region(1535, 1083, 1536, 1084); next = Step.ClaimRewards; }
                    else if (++stable >= 2) next = Step.ExitRewards;
                    break;
                case Step.ExitRewards:
                    if (!await ReadRewardsPageAsync(frame, screen, ocr, token)) break;
                    key = "Esc"; next = Step.ExitMenu; break;
                case Step.ExitMenu:
                    if (await Menu() is null) break;
                    key = "Esc"; next = Step.ExitWorld; break;
                case Step.ExitWorld:
                    if (await screen.World(token) is not null) return $"通行证任务领取了{completed}/4";
                    break;
            }
            if (!keyboard.IsWindowForeground(window.Hwnd)) continue;
            if (key is not null)
                await keyboard.SendSequenceAsync(window.Hwnd, key,
                    new() { Method = input.KeyboardInputMethod, HoldDurationMs = input.KeyboardHoldDurationMs }, token);
            if (click is not null)
            {
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(click.X + click.Width / 2d),
                    screen.ClientY(click.Y + click.Height / 2d), input.KeyboardInputMethod, token);
            }
            freshAfter = DateTimeOffset.UtcNow;
            if (key is not null || click is not null) await Task.Delay(next is Step.ClaimTasks or Step.ClaimRewards ? 2000 : 1000, token);
            if (next == step) continue;
            step = next;
            stable = 0;
            timer.Restart();
            progress(new("通行证奖励", Status()));
        }
    }
}
