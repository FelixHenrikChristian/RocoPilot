using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks.Engine;

namespace RocoPilot.Services.IndependentTasks;

public sealed class MailRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ILogger<MailRunner>? logger = null)
{
    private enum Step { World, Menu, Mailbox, AfterClaim, Exit }

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var loop = new TaskLoop<Step>("邮件领取", state, capture, keyboard, mouse, progress, Describe, Step.World, token, logger);
        var claimed = false;
        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Envelope() => screen.Find("mail/envelope", 115, 510, 247, 639, token);
            // 页头实测命中 0.95~1.00、被弹层遮住或不在邮箱时不到 0.52；放宽阈值，避免偶尔一帧没认出就当成已离开邮箱。
            Task<ImageMatchResult?> Header() => screen.Find("mail/header", 80, 5, 305, 107, token, .88);
            Task<ImageMatchResult?> Claim(string name) => screen.Find($"mail/{name}", 720, 998, 1020, 1126, token);

            var result = await loop.RunStepAsync(async () =>
            {
                switch (loop.Step)
                {
                    case Step.World:
                        if (await Envelope() is not null) loop.Advance(Step.Menu);
                        else if (await screen.World(token) is not null && await loop.KeyAsync("Esc")) loop.Advance(Step.Menu);
                        break;
                    case Step.Menu:
                        if (await Envelope() is { } envelope && await loop.ClickAsync(screen, envelope)) loop.Advance(Step.Mailbox);
                        break;
                    case Step.Mailbox:
                        if (await Header() is null) break;
                        if (await Claim("claim-all") is { } claim)
                        {
                            if (await loop.ClickAsync(screen, claim)) loop.Advance(Step.AfterClaim);
                        }
                        else if (await Claim("claim-disabled") is not null) loop.Advance(Step.Exit);
                        break;
                    case Step.AfterClaim:
                        if (await Header() is not null)
                        {
                            // 领取按钮还在：点击没有生效，等循环回到上一步重新点击。
                            if (await Claim("claim-all") is not null) break;
                            claimed = true;
                            loop.CommittedOutcome = "成功：已领取邮件附件";
                            // 按钮会先变灰、奖励弹层稍后才出来：邮箱稳定显示一会儿再去退出，
                            // 这期间弹层出来了由下面的分支关掉。
                            if (await Claim("claim-disabled") is not null && loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                                loop.Advance(Step.Exit);
                            break;
                        }
                        claimed = true;
                        loop.CommittedOutcome = "成功：已领取邮件附件";
                        // 已经退到菜单或大世界就不再按 Esc，避免把菜单重新打开。
                        if (await Envelope() is not null || await screen.World(token) is not null) loop.Advance(Step.Exit);
                        // 奖励弹层：出现动画期间 Esc 不起作用，按间隔一直按到它关掉。
                        else await loop.RepeatKeyAsync("Esc");
                        break;
                    case Step.Exit:
                        if (await screen.World(token) is not null)
                            return claimed ? "成功：已领取邮件附件" : "成功：没有可领取的邮件附件";
                        if (await Envelope() is not null || await Header() is not null) await loop.RepeatKeyAsync("Esc");
                        // 既不是邮箱也不是菜单：奖励弹层这时才出来，或被别的提示挡住。
                        else await loop.EscapeUnknownAsync();
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }

    private static string Describe(Step step) => step switch
    {
        Step.World => "等待大世界",
        Step.Menu => "打开邮箱",
        Step.Mailbox => "检查全部领取按钮",
        Step.AfterClaim => "等待领取结果",
        _ => "返回大世界"
    };
}
