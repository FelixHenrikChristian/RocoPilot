using System.Diagnostics;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

public sealed class GardeningRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse)
{
    private enum Step { Map, Garden, Entrance, Teleport, Approach, Harvest, Recall, Sow, Observe, Care }

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        var method = input.KeyboardInputMethod;
        keyboard.EnsureReady(method);
        await using var walk = new ForwardWalk(keyboard, window.Hwnd, method, token);
        var plots = new Queue<int>(Enumerable.Range(0, 15).Select(i =>
            (2 - i / 5) * 5 + (i / 5 == 1 ? 4 - i % 5 : i % 5)));
        var revisit = new HashSet<int>();
        var confirmed = 0;
        var step = Step.Map;
        var timer = Stopwatch.StartNew();
        var freshAfter = DateTimeOffset.MinValue;
        var stable = 0;
        var planted = false;
        var atPlot = false;
        var status = "打开家园地图";
        void Report(string message)
        {
            status = message;
            progress(new("自动种菜", message));
        }
        void Advance(Step next, string message)
        {
            step = next;
            timer.Restart();
            stable = 0;
            Report(message);
        }
        void NextPlot(bool success)
        {
            var plot = plots.Dequeue();
            if (success) confirmed++;
            else if (revisit.Add(plot)) plots.Enqueue(plot);
            else throw new InvalidOperationException($"第 {plot / 5 + 1} 行第 {plot % 5 + 1} 列播种未确认，已确认 {confirmed}/15 格。");
            planted = atPlot = false;
            if (plots.TryPeek(out var next))
                Advance(Step.Sow, $"第 {next / 5 + 1} 行第 {next % 5 + 1} 列，已确认 {confirmed}/15 格");
            else Advance(Step.Observe, "退到田边观察养护");
        }
        async Task Key(string key, int? duration = null)
        {
            if (!keyboard.IsWindowForeground(window.Hwnd)) throw new InvalidOperationException("输入前游戏窗口已离开前台。");
            await keyboard.SendSequenceAsync(window.Hwnd, key, new() { Method = method,
                HoldDurationMs = duration ?? input.KeyboardHoldDurationMs }, token);
            freshAfter = DateTimeOffset.UtcNow;
            await Task.Delay(950, token);
        }
        Report(status);
        while (true)
        {
            await Task.Delay(step == Step.Care ? 2200 : walk.Active ? 120 : 350, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                if (timer.IsRunning) progress(new("自动种菜", "已暂停，等待切回游戏"));
                timer.Stop();
                stable = 0;
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (!timer.IsRunning) { timer.Start(); Report(status); }
            if (timer.Elapsed.TotalSeconds > (step == Step.Care ? 600 : 60))
                throw new TimeoutException($"自动种菜超时：{status}，已确认 {confirmed}/15 格。");
            using var frame = capture.Capture(window, state.Options.CaptureMethod);
            if (frame is null || frame.CapturedAt <= freshAfter) continue;
            freshAfter = frame.CapturedAt;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int x, int y, int right, int bottom, double score = .94) =>
                screen.Find(name, x, y, right, bottom, token, score);
            async Task Click(ImageMatchResult m)
            {
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(m.X + m.Width / 2d), screen.ClientY(m.Y + m.Height / 2d), method, token);
                freshAfter = DateTimeOffset.UtcNow;
                await Task.Delay(950, token);
            }
            if (step <= Step.Teleport)
            {
                switch (step)
                {
                    case Step.Map:
                        if (await screen.Map(token) != null)
                            Advance(Step.Garden, "选择家园种植园");
                        else if (await screen.World(token) != null)
                        {
                            await Key("M");
                            Advance(Step.Garden, "选择家园种植园");
                        }
                        break;
                    case Step.Garden:
                        var (selected, target) = await screen.SelectGarden(token);
                        if (target != null) await Click(target);
                        if (selected) Advance(Step.Entrance, "定位种植园入口");
                        break;
                    case Step.Entrance:
                        if (await screen.Garden(token) != null &&
                            await Find("gardening/entrance", 350, 210, 1580, 1080) is { } marker)
                        {
                            await Click(marker);
                            Advance(Step.Teleport, "传送到种植园入口");
                        }
                        break;
                    case Step.Teleport:
                        if (await Find("gardening/entrance-detail", 1300, 0, 1450, 110) != null &&
                            await screen.Teleport(token) is { } teleport)
                        {
                            await Click(teleport);
                            Advance(Step.Approach, "从入口接近田地");
                        }
                        break;
                }
                continue;
            }

            if (step == Step.Sow && await Find("gardening/pet-panel", 1650, 0, 1980, 145, .89) != null)
            {
                await Key("Esc");
                stable = 0;
                planted = false;
                continue;
            }
            if (await screen.World(token) == null)
            {
                await walk.StopAsync();
                stable = 0;
                continue;
            }
            var vision = new GardenVision(frame, window);
            var pos = vision.Locate();
            // 从南侧入口直行，提前一行松键，停稳后再微调到收菜位置。
            if (step == Step.Approach && (pos == null || pos.Value.Row > 3.5))
            {
                await walk.AdvanceAsync(frame.CapturedAt);
                continue;
            }
            if (walk.Active)
            {
                await walk.StopAsync();
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (pos is not { } location) { stable = 0; continue; }
            switch (step)
            {
                case Step.Approach:
                    if (location.DirectionTo(2.5, 2.5) is { } approach)
                    {
                        await Key(approach, 160);
                        break;
                    }
                    if (await screen.HasHarvestPrompt(token))
                    {
                        await Key("1");
                        mouse.LookDown(window.Hwnd, method);
                        await Task.Delay(400, token);
                        await mouse.PressAtCurrentPositionAsync(window.Hwnd, 1100, method, token);
                        freshAfter = DateTimeOffset.UtcNow;
                        await Task.Delay(1800, token);
                        Advance(Step.Harvest, "等待精灵收菜");
                    }
                    else if (++stable >= 3) Advance(Step.Sow, "检查第一块地");
                    break;
                case Step.Harvest:
                    if (await screen.HasHarvestPrompt(token)) stable = 0;
                    else if (++stable >= 3) Advance(Step.Recall, "收回精灵");
                    break;
                case Step.Recall:
                    await mouse.PressAtCurrentPositionAsync(window.Hwnd, 80, method, token);
                    freshAfter = DateTimeOffset.UtcNow;
                    await Task.Delay(1200, token);
                    Advance(Step.Sow, "检查第一块地");
                    break;
                case Step.Sow:
                    var id = plots.Peek();
                    if (location.DirectionTo(id % 5 + .5, id / 5 + .5) is { } direction)
                    {
                        atPlot = false;
                        stable = 0;
                        await Key(direction, 150);
                        break;
                    }
                    if (!atPlot)
                    {
                        atPlot = true;
                        timer.Restart();
                        break;
                    }
                    var canPlant = await screen.CanPlant(token);
                    if (!canPlant && (await Find("gardening/shovel", 1610, 950, 1850, 1130, .90) != null ||
                        await Find("gardening/growth", 775, 160, 1240, 555, .94) != null))
                    {
                        NextPlot(true);
                        break;
                    }
                    if (canPlant && !planted)
                    {
                        if (++stable >= 2)
                        {
                            await Key("F");
                            planted = true;
                            timer.Restart();
                            stable = 0;
                        }
                    }
                    else stable = 0;
                    if (timer.Elapsed.TotalSeconds > 7) NextPlot(false);
                    break;
                case Step.Observe:
                    if (location.DirectionTo(2.5, 4.3) is { } away) await Key(away, 150);
                    else Advance(Step.Care, "等待整田浇水施肥");
                    break;
                case Step.Care:
                    if (location.DirectionTo(2.5, 4.3) != null)
                        throw new InvalidOperationException("已离开田边观察位置，养护未确认。");
                    if (vision.HasCareBubble())
                        stable = 0;
                    else if (++stable >= 4)
                        return "成功：15/15 格已种植，浇水施肥已完成";
                    break;
            }
        }
    }
}
