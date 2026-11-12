using System.Diagnostics;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

public sealed class MailRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse)
{
    private enum Step { World, Menu, Mailbox, AfterClaim, Exit }

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        keyboard.EnsureReady(input.KeyboardInputMethod);
        var step = Step.World;
        var claimed = false;
        var timer = Stopwatch.StartNew();
        var freshAfter = DateTimeOffset.MinValue;
        string Status() => step switch
        {
            Step.World => "等待大世界",
            Step.Menu => "打开邮箱",
            Step.Mailbox => "检查全部领取按钮",
            Step.AfterClaim => "等待领取结果",
            _ => "返回大世界"
        };
        progress(new("邮件领取", Status()));
        while (true)
        {
            await Task.Delay(350, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                if (timer.IsRunning) progress(new("邮件领取", "已暂停，等待切回游戏"));
                timer.Stop();
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (!timer.IsRunning)
            {
                timer.Start();
                progress(new("邮件领取", Status()));
            }
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException($"邮件领取等待超时：{Status()}。");

            using var frame = capture.Capture(window, state.Options.CaptureMethod);
            if (frame is null || frame.CapturedAt <= freshAfter) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Envelope() => screen.Find("mail/envelope", 115, 510, 247, 639, token);
            Task<ImageMatchResult?> Header() => screen.Find("mail/header", 80, 5, 305, 107, token);
            Task<ImageMatchResult?> Claim(string name) => screen.Find($"mail/{name}", 720, 998, 1020, 1126, token);
            var next = step;
            ImageMatchResult? click = null;
            switch (step)
            {
                case Step.World:
                    if (await screen.World(token) is null) continue;
                    next = Step.Menu;
                    break;
                case Step.Menu:
                    click = await Envelope();
                    if (click is null) continue;
                    next = Step.Mailbox;
                    break;
                case Step.Mailbox:
                    if (await Header() is null) continue;
                    click = await Claim("claim-all");
                    if (click is null && await Claim("claim-disabled") is null) continue;
                    next = click is null ? Step.Exit : Step.AfterClaim;
                    break;
                case Step.AfterClaim:
                    if (await Header() is not null && await Claim("claim-all") is not null) continue;
                    next = Step.Exit;
                    break;
                case Step.Exit:
                    if (await screen.World(token) is not null)
                        return claimed ? "成功：已领取邮件附件" : "成功：没有可领取的邮件附件";
                    if (await Envelope() is null && (await Header() is null || await Claim("claim-disabled") is null)) continue;
                    break;
            }

            if (!keyboard.IsWindowForeground(window.Hwnd)) continue;
            if (click is null)
                await keyboard.SendSequenceAsync(window.Hwnd, "Esc",
                    new() { Method = input.KeyboardInputMethod, HoldDurationMs = input.KeyboardHoldDurationMs }, token);
            else
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(click.X + click.Width / 2d),
                    screen.ClientY(click.Y + click.Height / 2d), input.KeyboardInputMethod, token);
            freshAfter = DateTimeOffset.UtcNow;
            await Task.Delay(next == Step.AfterClaim ? 2000 : 800, token);
            if (next == step) continue;
            step = next;
            if (step == Step.AfterClaim) claimed = true;
            timer.Restart();
            progress(new("邮件领取", Status()));
        }
    }
}
