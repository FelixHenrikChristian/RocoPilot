using System.Diagnostics;
using System.Globalization;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.IndependentTasks;

public sealed class CropSaleRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ITextRecognitionService ocr)
{
    private const int ActionPauseMs = 1000;
    private const int FlowerClickIntervalMs = 200;
    private enum Step { Map, Garden, Marker, Teleport, Approach, Dialogue, FindFlower, Quantity, Confirm, Reward, Exit }

    public async Task<string> RunAsync(RuntimeTaskState state, string flower, int quantity,
        Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        var method = input.KeyboardInputMethod;
        keyboard.EnsureReady(method);
        await using var walk = new ForwardWalk(keyboard, window.Hwnd, method, token);
        var step = Step.Map;
        var status = "打开家园地图";
        var timer = Stopwatch.StartNew();
        var freshAfter = DateTimeOffset.MinValue;
        var remaining = quantity;
        var outcome = string.Empty;
        void Advance(Step next, string message)
        {
            step = next;
            status = message;
            timer.Restart();
            progress(new("自动出售", status));
        }
        async Task AfterInput()
        {
            freshAfter = DateTimeOffset.UtcNow;
            // 主动留给游戏的响应时间不占用查花、等待界面等步骤的超时预算。
            timer.Stop();
            await Task.Delay(ActionPauseMs, token);
            timer.Start();
        }
        async Task Key(string key)
        {
            if (!keyboard.IsWindowForeground(window.Hwnd)) throw new InvalidOperationException("游戏窗口已离开前台。");
            await keyboard.SendSequenceAsync(window.Hwnd, key, new() { Method = method, HoldDurationMs = input.KeyboardHoldDurationMs }, token);
            await AfterInput();
        }

        progress(new("自动出售", status));
        while (true)
        {
            await Task.Delay(step switch { Step.Quantity => FlowerClickIntervalMs, Step.Approach => 120, _ => 350 }, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                await walk.StopAsync();
                if (timer.IsRunning) progress(new("自动出售", "已暂停，等待切回游戏"));
                timer.Stop();
                freshAfter = DateTimeOffset.UtcNow;
                continue;
            }
            if (!timer.IsRunning) { timer.Start(); progress(new("自动出售", status)); }
            if (timer.Elapsed.TotalSeconds > 45) throw new TimeoutException($"自动出售等待超时：{status}。");

            using var frame = capture.Capture(window, state.Options.CaptureMethod);
            if (frame == null || frame.CapturedAt <= freshAfter) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int x, int y, int right, int bottom, double score = .94) =>
                screen.Find($"crop-sale/{name}", x, y, right, bottom, token, score);
            Task<ImageMatchResult?> Station() => Find("station", 75, 0, 408, 106);
            Task<ImageMatchResult?> Dialogue() => Find("dialogue", 1440, 710, 1950, 925);
            async Task Click(ImageMatchResult match)
            {
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(match.X + match.Width / 2d),
                    screen.ClientY(match.Y + match.Height / 2d), method, token);
                await AfterInput();
            }
            async Task Add(double x, double y)
            {
                await mouse.ClickAsync(window.Hwnd, screen.ClientX(x), screen.ClientY(y), method, token);
                remaining--;
                freshAfter = DateTimeOffset.UtcNow;
                if (remaining == 0) await AfterInput();
                timer.Restart();
            }

            switch (step)
            {
                case Step.Map:
                    if (await screen.Map(token) == null)
                    {
                        if (await screen.World(token) == null) break;
                        await Key("M");
                    }
                    Advance(Step.Garden, "选择家园种植园");
                    break;
                case Step.Garden:
                    var (selected, target) = await screen.SelectGarden(token);
                    if (target != null) await Click(target);
                    if (selected) Advance(Step.Marker, "定位老山姆");
                    break;
                case Step.Marker:
                    if (await screen.Garden(token) != null && await Find("marker", 350, 130, 1620, 1030, .94) is { } marker)
                    {
                        await Click(marker);
                        Advance(Step.Teleport, "传送到老山姆");
                    }
                    break;
                case Step.Teleport:
                    if (await Find("detail", 1290, 0, 1710, 120) != null && await screen.Teleport(token) is { } teleport)
                    {
                        await Click(teleport);
                        Advance(Step.Approach, "向前走到老山姆");
                    }
                    break;
                case Step.Approach:
                    if (await screen.World(token) == null) { await walk.StopAsync(); break; }
                    if (await Find("interact", 1205, 578, 1520, 638, .953) == null)
                    {
                        await walk.AdvanceAsync(frame.CapturedAt);
                        break;
                    }
                    if (walk.Active)
                    {
                        await walk.StopAsync();
                        await AfterInput();
                        break;
                    }
                    await Key("F");
                    Advance(Step.Dialogue, "进入农作物出售站");
                    break;
                case Step.Dialogue:
                    if (await Dialogue() != null)
                    {
                        await Key("1");
                        Advance(Step.FindFlower, $"查找{flower}");
                    }
                    break;
                case Step.FindFlower:
                    if (await Station() is not { } header || await Find("empty", 1400, 625, 1920, 785) == null) break;
                    // 从空选开始。保留顶部空间让选中后的减号完整可见。
                    var card = await Find($"flowers/{flower}", 110, 139, 734, 1003, .988);
                    if (card != null)
                    {
                        await Add(card.X + card.Width / 2d, card.Y + card.Height / 2d);
                        Advance(Step.Quantity, $"选择{flower}，目标 {quantity} 朵");
                        break;
                    }
                    if (timer.Elapsed.TotalSeconds > 20)
                    {
                        outcome = $"跳过：列表中未找到{flower}";
                        await Key("Esc");
                        Advance(Step.Exit, "退出农作物出售站");
                        break;
                    }
                    await mouse.ScrollAsync(window.Hwnd, screen.ClientX(header.X + 300 * screen.Scale),
                        screen.ClientY(header.Y + 550 * screen.Scale), -120, method, token);
                    await AfterInput();
                    break;
                case Step.Quantity:
                    if (await Station() == null || await Find("sell", 90, 1020, 390, 1130, .98) is not { } sell) break;
                    // 首次选择后列表底部会为预期收入让位，用减号重新定位选中的卡片。
                    if (await Find("minus", 98, 104, 740, 967, .965) is not { } minus) break;
                    var cx = minus.X + minus.Width / 2d + 82 * screen.Scale;
                    var cy = minus.Y + minus.Height / 2d + 71 * screen.Scale;
                    if (remaining > 0) await Add(cx, cy);
                    else
                    {
                        var income = await ReadIncomeAsync(frame, screen, ocr, token);
                        var price = income is null ? "预期收入未识别" : $"预期收入 {income} 金币";
                        outcome = $"成功：已领取{flower}的出售奖励，{price}";
                        await Click(sell);
                        Advance(Step.Confirm, "等待出售确认");
                    }
                    break;
                case Step.Confirm:
                    if (await Find("confirm-title", 755, 360, 1280, 508) != null &&
                        await Find("confirm", 1030, 890, 1340, 1000, .98) is { } confirm)
                    {
                        await Click(confirm);
                        Advance(Step.Reward, "等待出售奖励");
                    }
                    break;
                case Step.Reward:
                    if (await Find("reward", 820, 370, 1240, 540) != null)
                    {
                        await Key("Esc");
                        Advance(Step.Exit, "退出农作物出售站");
                    }
                    break;
                case Step.Exit:
                    if (await screen.World(token) != null) return outcome;
                    if (await Station() != null || await Dialogue() != null) await Key("Esc");
                    break;
            }
        }
    }

    internal static async Task<long?> ReadIncomeAsync(CapturedFrame frame, DailyTaskScreen screen,
        ITextRecognitionService ocr, CancellationToken token)
    {
        // 金额右对齐，金币图标随位数左移。只把图标右侧的数字交给单行 OCR。
        var coin = await screen.Find("crop-sale/income-coin", 300, 976, 690, 1028, token);
        if (coin == null) return null;
        var digits = screen.Region(300, 986, 710, 1018);
        var left = coin.X + coin.Width + (int)Math.Round(2 * screen.Scale);
        digits.Width -= left - digits.X;
        digits.X = left;
        if (digits.Width <= 0) return null;
        try
        {
            var result = await ocr.RecognizeAsync(frame, digits, TextRecognitionMethod.OnnxOcrV5, token);
            return long.TryParse(result.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && amount > 0
                ? amount : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 金额只用于成交后的日志及每日汇总。
            return null;
        }
    }
}
