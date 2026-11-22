using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks.Engine;

namespace RocoPilot.Services.IndependentTasks;

public sealed class GardeningRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ILogger<GardeningRunner>? logger = null)
{
    private enum Step { Map, Garden, Entrance, Teleport, OpenPocket, PickSeed, EquipSeed, WaitForGarden, Approach, Harvest, Recall, Sow, Observe, Care }

    // 田里的操作要等角色和镜头停稳：按键后隔这么久再识别（原先的操作等待加一次轮询）。
    private const int FieldSettleMs = 1300;
    // 格间移动只等一次定位，下一轮仍重新定位。
    private const int MoveSettleMs = 450;

    public async Task<string> RunAsync(RuntimeTaskState state, IndependentTaskSettings settings,
        Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var method = state.Options.AutoBattleSettings.KeyboardInputMethod;
        keyboard.EnsureReady(method);
        await using var walk = new ForwardWalk(keyboard, window.Hwnd, method, token);
        var route = Enumerable.Range(0, 15).Select(i =>
            (2 - i / 5) * 5 + (i / 5 == 1 ? 4 - i % 5 : i % 5)).ToArray();
        var plots = new Queue<int>(route);
        var revisit = new HashSet<int>();
        var pendingSows = new HashSet<int>();
        var newlySown = 0;
        var seedIndex = IndependentTaskSettings.SaleFlowers.ToList().IndexOf(settings.GardenSeed);
        var stable = 0;
        var planted = false;
        var atPlot = false;
        var loop = new TaskLoop<Step>("自动种菜", state, capture, keyboard, mouse, progress, _ => "打开家园地图", Step.Map, token, logger)
        {
            Timeout = step => TimeSpan.FromSeconds(step == Step.Care ? 600 : 60),
            // 地图和口袋是静止界面，按引擎的节奏识别；田里靠连续几帧做判断，保持原来的间隔并且每帧只算一次。
            Poll = step => step == Step.Care ? 2200 : step < Step.Approach ? TaskPacing.PollMs : walk.Active ? 120 : 350,
            DistinctFrames = step => step >= Step.Approach,
            Paused = async () =>
            {
                stable = 0;
                await walk.StopAsync();
            }
        };
        // 田里的输入由这里的状态自己核对结果，不交给引擎照原样补发。
        void Advance(Step next, string message)
        {
            stable = 0;
            loop.Advance(next, message, retryable: loop.Step < Step.Approach);
        }
        void ReportPlot(int plot)
        {
            var message = $"第 {Array.IndexOf(route, plot) + 1} 块地，已种 {newlySown}/15";
            if (message != loop.Status) loop.Report(message);
        }
        void NextPlot(bool success)
        {
            var plot = plots.Dequeue();
            if (success)
            {
                if (pendingSows.Remove(plot))
                {
                    newlySown++;
                    ReportPlot(plot);
                }
            }
            else if (revisit.Add(plot)) plots.Enqueue(plot);
            planted = atPlot = false;
            if (plots.Count > 0)
            {
                loop.KeepAlive();
                stable = 0;
            }
            else Advance(Step.Observe, "退到田边观察养护");
        }
        Task<bool> Key(string key, int? duration = null, int settleMs = FieldSettleMs) =>
            loop.KeyAsync(key, duration, react: false, settleMs: settleMs);

        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int x, int y, int right, int bottom, double score = .94) =>
                screen.Find(name, x, y, right, bottom, token, score);

            var result = await loop.RunStepAsync(async () =>
            {
                if (loop.Step <= Step.Teleport)
                {
                    switch (loop.Step)
                    {
                        case Step.Map:
                            if (await screen.Map(token) != null) Advance(Step.Garden, "选择家园种植园");
                            else if (await screen.World(token) != null && await loop.KeyAsync("M"))
                                Advance(Step.Garden, "选择家园种植园");
                            break;
                        case Step.Garden:
                            var (selected, target) = await screen.SelectGarden(token);
                            if (selected)
                            {
                                if (target == null || await loop.ClickAsync(screen, target)) Advance(Step.Entrance, "定位种植园入口");
                            }
                            // 展开地图列表的三角是开关，等列表展开后再判断，不连点。
                            else if (target != null && loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                                await loop.ClickAsync(screen, target);
                            break;
                        case Step.Entrance:
                            if (await screen.Garden(token) != null &&
                                await Find("gardening/entrance", 350, 210, 1580, 1080) is { } marker &&
                                await loop.ClickAsync(screen, marker))
                                Advance(Step.Teleport, "传送到种植园入口");
                            break;
                        case Step.Teleport:
                            if (await Find("gardening/entrance-detail", 1300, 0, 1450, 110) != null &&
                                await screen.Teleport(token) is { } teleport &&
                                await loop.ClickAsync(screen, teleport))
                            {
                                if (settings.ReuseEquippedSeed) Advance(Step.Approach, "从入口接近田地");
                                else Advance(Step.OpenPocket, "打开种子口袋");
                            }
                            break;
                    }
                    return null;
                }

                if (loop.Step is >= Step.OpenPocket and <= Step.WaitForGarden)
                {
                    var pocket = new GardenSeedPocket(frame, screen);
                    if (await pocket.Title(token) == null)
                    {
                        if (await screen.World(token) != null)
                        {
                            // E 是开关键：口袋没打开时隔足够久再按，避免刚打开又被关上。
                            if (loop.Step == Step.OpenPocket)
                                await loop.RepeatKeyAsync("E", (int)TaskPacing.KeyRetryDelay.TotalMilliseconds);
                            else if (loop.Step == Step.WaitForGarden) Advance(Step.Approach, "从入口接近田地");
                        }
                        return null;
                    }
                    switch (loop.Step)
                    {
                        case Step.OpenPocket:
                            if (await pocket.Remove(token) is { } remove)
                            {
                                if (loop.InputIdleFor(TaskPacing.RepeatKeyMs)) await loop.ClickAsync(screen, remove);
                            }
                            else if (await pocket.Equip(token) != null)
                                Advance(Step.PickSeed, $"选择{settings.GardenSeed}种子");
                            break;
                        case Step.PickSeed:
                            if (await pocket.Seed(seedIndex, token) is { } seed)
                            {
                                if (await loop.ClickAsync(screen, seed))
                                    Advance(Step.EquipSeed, $"装备{settings.GardenSeed}，每格 {settings.SeedsPerPlot} 颗");
                            }
                            else
                            {
                                var (x, y) = pocket.ClientPoint(757, 619);
                                await loop.ScrollAsync(x, y, seedIndex < 5 ? 720 : -720);
                            }
                            break;
                        case Step.EquipSeed:
                            if (await pocket.Equip(token) is not { } equip || await pocket.Detail(seedIndex, token) == null) break;
                            if (!pocket.QuantitySelected(settings.SeedsPerPlot))
                            {
                                var (x, y) = pocket.ClientPoint(1228 + (settings.SeedsPerPlot - 1) * 158, 577);
                                await loop.ClickAsync(x, y);
                                break;
                            }
                            if (await loop.ClickAsync(screen, equip)) Advance(Step.WaitForGarden, "等待返回种植园");
                            break;
                    }
                    return null;
                }

                if (loop.Step == Step.Sow && await Find("gardening/pet-panel", 1650, 0, 1980, 145, .89) != null)
                {
                    if (!await Key("Esc")) return null;
                    stable = 0;
                    if (planted) pendingSows.Remove(plots.Peek());
                    planted = false;
                    return null;
                }
                if (await screen.World(token) == null)
                {
                    await walk.StopAsync();
                    stable = 0;
                    return null;
                }
                var vision = new GardenVision(frame, window);
                var pos = vision.Locate();
                // 从南侧入口直行，提前一行松键，停稳后再微调到收菜位置。
                if (loop.Step == Step.Approach && (pos == null || pos.Value.Row > 3.5))
                {
                    await walk.AdvanceAsync(frame.CapturedAt);
                    return null;
                }
                // 退到观察点时连续走，距离当前轴目标一格内松键，再沿用短步定位。
                if (loop.Step == Step.Observe && pos is { } observing &&
                    observing.DirectionTo(2.5, 4.3) is { } awayDirection &&
                    (awayDirection is "A" or "D" ? Math.Abs(observing.Column - 2.5) : Math.Abs(observing.Row - 4.3)) > 1)
                {
                    await walk.AdvanceAsync(frame.CapturedAt, awayDirection);
                    return null;
                }
                if (walk.Active)
                {
                    await walk.StopAsync();
                    loop.RequireFreshFrame();
                    return null;
                }
                if (pos is not { } location) { stable = 0; return null; }
                switch (loop.Step)
                {
                    case Step.Approach:
                        if (location.DirectionTo(2.5, 2.5) is { } approach)
                        {
                            await Key(approach, 160);
                            break;
                        }
                        if (await screen.HasHarvestPrompt(token))
                        {
                            if (!await Key("1")) break;
                            try
                            {
                                await Task.Delay(950, token);
                                mouse.LookDown(window.Hwnd, method);
                                await Task.Delay(400, token);
                                await mouse.PressAtCurrentPositionAsync(window.Hwnd, 1100, method, token);
                            }
                            catch (InvalidOperationException) when (!loop.IsForeground)
                            {
                                // 中途被切走：暂停后回到这一步重新判断，提示还在就再放一次精灵。
                                break;
                            }
                            loop.NoteInput(settleMs: 1800 + 350);
                            Advance(Step.Harvest, "等待精灵收菜");
                        }
                        else if (++stable >= 3) Advance(Step.Sow, "定位当前地块");
                        break;
                    case Step.Harvest:
                        if (await screen.HasHarvestPrompt(token)) stable = 0;
                        else if (++stable >= 3) Advance(Step.Recall, "收回精灵");
                        break;
                    case Step.Recall:
                        try
                        {
                            await mouse.PressAtCurrentPositionAsync(window.Hwnd, 80, method, token);
                        }
                        catch (InvalidOperationException) when (!loop.IsForeground)
                        {
                            break;
                        }
                        loop.NoteInput(settleMs: 1200 + 350);
                        Advance(Step.Sow, "定位当前地块");
                        break;
                    case Step.Sow:
                        if (location.Column is >= 0 and < 5 && location.Row is >= 0 and < 3)
                            ReportPlot((int)location.Row * 5 + (int)location.Column);
                        var id = plots.Peek();
                        if (location.DirectionTo(id % 5 + .5, id / 5 + .5) is { } direction)
                        {
                            atPlot = false;
                            stable = 0;
                            // 缩短移动后的等待；下一轮仍重新定位，到格后沿用原有播种确认节奏。
                            await Key(direction, 100, MoveSettleMs);
                            break;
                        }
                        if (!atPlot)
                        {
                            atPlot = true;
                            loop.KeepAlive();
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
                            if (++stable >= 2 && await Key("F"))
                            {
                                pendingSows.Add(id);
                                planted = true;
                                loop.KeepAlive();
                                stable = 0;
                            }
                        }
                        else stable = 0;
                        // 这一格 7 秒内没种上（F 没生效或被挡住）：先去下一格，整圈走完再回来补一次。
                        if (loop.StepElapsed.TotalSeconds > 7) NextPlot(false);
                        break;
                    case Step.Observe:
                        if (location.DirectionTo(2.5, 4.3) is { } away) await Key(away, 150);
                        else Advance(Step.Care, "等待整田浇水施肥");
                        break;
                    case Step.Care:
                        // 重跑会重新走一遍田：已种的格子直接跳过，再回到观察点确认养护。
                        if (location.DirectionTo(2.5, 4.3) != null)
                            throw new TaskStalledException("已离开田边观察位置，养护未确认。");
                        if (vision.HasCareBubble())
                            stable = 0;
                        else if (++stable >= 4)
                            return $"种了 {newlySown}/15";
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }
}
