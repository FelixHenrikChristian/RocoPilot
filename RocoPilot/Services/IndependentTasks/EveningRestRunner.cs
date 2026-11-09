using System.Diagnostics;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

public sealed class EveningRestRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse)
{
    private enum Step { Open, Explore, CheckEvent, Teleport, Arrival, Monitor, Exit }

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        keyboard.EnsureReady(input.KeyboardInputMethod);
        var step = Step.Open;
        var arrived = false;
        var freshAfter = DateTimeOffset.MinValue;
        var timer = Stopwatch.StartNew();
        var exitReason = "";
        progress(new("晚风小憩", "打开活动"));
        while (true)
        {
            await Task.Delay(step == Step.Monitor ? 5000 : 400, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                if (timer.IsRunning) progress(new("晚风小憩", "已暂停，等待切回游戏"));
                timer.Stop();
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (!timer.IsRunning)
            {
                timer.Start();
                progress(new("晚风小憩", Describe(step)));
            }
            if (timer.Elapsed.TotalSeconds > 60)
                throw new TimeoutException($"晚风小憩等待超时：{Describe(step)}。");

            using var frame = await Task.Run(() => capture.Capture(window, state.Options.CaptureMethod), token);
            if (frame is null || frame.CapturedAt <= freshAfter) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int left, int top, int right, int bottom) =>
                screen.Find($"evening-rest/{name}", left, top, right, bottom, token);
            Task<ImageMatchResult?> Explore() => Find("explore", 175, 34, 310, 105);
            Task<ImageMatchResult?> Event() => Find("event", 85, 118, 445, 219);
            Task<ImageMatchResult?> Full() => Find("full", 1680, 820, 1960, 915);
            async Task<bool> Key(string key)
            {
                if (!keyboard.IsWindowForeground(window.Hwnd)) return false;
                await keyboard.SendSequenceAsync(window.Hwnd, key,
                    new() { Method = input.KeyboardInputMethod, HoldDurationMs = input.KeyboardHoldDurationMs }, token);
                freshAfter = DateTimeOffset.UtcNow;
                await Task.Delay(1000, token);
                return true;
            }

            async Task Click(ImageMatchResult match)
            {
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(match.X + match.Width / 2d),
                    screen.ClientY(match.Y + match.Height / 2d), input.KeyboardInputMethod, token);
                freshAfter = DateTimeOffset.UtcNow;
                await Task.Delay(1000, token);
            }

            var previous = step;
            switch (step)
            {
                case Step.Open:
                case Step.Arrival:
                    if (await screen.World(token) is not null)
                    {
                        arrived = step == Step.Arrival;
                        if (await Key("F2")) step = Step.Explore;
                    }
                    else if (step == Step.Open && (await Explore() is not null ||
                        await Find("tab", 300, 1025, 470, 1140) is not null)) step = Step.Explore;
                    break;
                case Step.Explore:
                    if (await Explore() is not null) step = Step.CheckEvent;
                    else if (await Find("tab", 300, 1025, 470, 1140) is { } tab) await Click(tab);
                    break;
                case Step.CheckEvent:
                case Step.Monitor:
                    if (await Explore() is null) break;
                    if (await Event() is null)
                    {
                        if (timer.Elapsed.TotalSeconds < 3) break;
                        exitReason = "跳过：默认活动不是晚风小憩";
                        step = Step.Exit;
                    }
                    else if (await Full() is not null)
                    {
                        exitReason = "成功：本日星光已达 1200/1200";
                        step = Step.Exit;
                    }
                    else if (arrived)
                    {
                        // 有效活动页可以长时间停留；只对界面丢失使用导航超时。
                        timer.Restart();
                        step = Step.Monitor;
                    }
                    else if (await Find("go", 1470, 995, 1620, 1130) is { } go)
                    {
                        await Click(go);
                        step = Step.Teleport;
                    }
                    else if (timer.Elapsed.TotalSeconds >= 5)
                    {
                        exitReason = "跳过：未找到前往按钮";
                        step = Step.Exit;
                    }
                    break;
                case Step.Teleport:
                    if (await Find("shelter", 1310, 15, 1730, 116) is not null &&
                        await screen.Teleport(token) is { } teleport)
                    {
                        await Click(teleport);
                        step = Step.Arrival;
                    }
                    break;
                case Step.Exit:
                    if (await screen.World(token) is not null) return exitReason;
                    if (await Explore() is not null) await Key("Esc");
                    break;
            }
            if (step != previous)
            {
                timer.Restart();
                progress(new("晚风小憩", step == Step.Exit ? exitReason : Describe(step)));
            }
        }
    }

    private static string Describe(Step step) => step switch
    {
        Step.Open or Step.Explore => "打开活动探索页",
        Step.CheckEvent => "确认晚风小憩",
        Step.Teleport => "传送到星光庇护所",
        Step.Arrival => "等待传送完成",
        Step.Monitor => "等待本日星光达到 1200/1200",
        _ => "返回大世界"
    };
}
