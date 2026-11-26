using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.IndependentTasks.Engine;

namespace RocoPilot.Services.IndependentTasks;

public sealed class CropSaleRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ITextRecognitionService ocr,
    ILogger<CropSaleRunner>? logger = null)
{
    private const int FlowerClickIntervalMs = 150;
    private const int FlowerScrollDelta = -6 * 120;
    private enum Step { Map, Garden, Marker, Teleport, Approach, Dialogue, FindFlower, Quantity, Confirm, Reward, Exit }

    public async Task<string> RunAsync(RuntimeTaskState state, string flower, int quantity,
        Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var loop = new TaskLoop<Step>("自动出售", state, capture, keyboard, mouse, progress, _ => "打开家园地图", Step.Map, token, logger)
        {
            Timeout = _ => TimeSpan.FromSeconds(45)
        };
        var lastWalkEnded = DateTimeOffset.MinValue;
        var sinceFlowerClick = new Stopwatch();
        var remaining = quantity;
        var outcome = string.Empty;

        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int x, int y, int right, int bottom, double score = .94) =>
                screen.Find($"crop-sale/{name}", x, y, right, bottom, token, score);
            Task<ImageMatchResult?> Station() => Find("station", 75, 0, 408, 106);
            Task<ImageMatchResult?> Dialogue() => Find("dialogue", 1440, 710, 1950, 925);
            Task<ImageMatchResult?> Empty() => Find("empty", 1400, 625, 1920, 785);
            async Task<bool> Add(double x, double y)
            {
                // 从上一次点击开始计时，鼠标定位和按压都计入间隔。
                var waitMs = FlowerClickIntervalMs - sinceFlowerClick.ElapsedMilliseconds;
                if (sinceFlowerClick.IsRunning && waitMs > 0) await Task.Delay((int)waitMs, token);
                token.ThrowIfCancellationRequested();
                sinceFlowerClick.Restart();
                if (!await loop.ClickAsync(screen.ClientX(x), screen.ClientY(y), react: false)) return false;
                remaining--;
                loop.KeepAlive();
                return true;
            }

            var result = await loop.RunStepAsync(async () =>
            {
                switch (loop.Step)
                {
                    case Step.Map:
                        if (await screen.Map(token) != null) loop.Advance(Step.Garden, "选择家园种植园");
                        else if (await screen.World(token) != null && await loop.KeyAsync("M"))
                            loop.Advance(Step.Garden, "选择家园种植园");
                        break;
                    case Step.Garden:
                        var (selected, target) = await screen.SelectGarden(token);
                        if (selected)
                        {
                            if (target == null || await loop.ClickAsync(screen, target)) loop.Advance(Step.Marker, "定位老山姆");
                        }
                        // 展开地图列表的三角是开关，等列表展开后再判断，不连点。
                        else if (target != null && loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                            await loop.ClickAsync(screen, target);
                        break;
                    case Step.Marker:
                        if (await screen.Garden(token) != null && await Find("marker", 350, 130, 1620, 1030, .94) is { } marker &&
                            await loop.ClickAsync(screen, marker))
                            loop.Advance(Step.Teleport, "传送到老山姆");
                        break;
                    case Step.Teleport:
                        if (await Find("detail", 1290, 0, 1710, 120) != null && await screen.Teleport(token) is { } teleport &&
                            await loop.ClickAsync(screen, teleport))
                            loop.Advance(Step.Approach, "向前走到老山姆");
                        break;
                    case Step.Approach:
                        // 卡顿时可能反复取到迈步前的画面；等松开 W 后的新画面再决定。
                        if (frame.CapturedAt <= lastWalkEnded) break;
                        // 看到对话选项才算交互成功；F 没有生效时提示仍在，按间隔再按。
                        if (await Dialogue() != null)
                        {
                            loop.Advance(Step.Dialogue, "进入农作物出售站");
                            break;
                        }
                        if (await screen.World(token) == null) break;
                        if (await Find("interact", 1205, 578, 1520, 638, .953) != null)
                            await loop.RepeatKeyAsync("F");
                        else if (await loop.KeyAsync("W", 300))
                            lastWalkEnded = DateTimeOffset.UtcNow;
                        break;
                    case Step.Dialogue:
                        if (await Dialogue() != null && await loop.KeyAsync("1"))
                            loop.Advance(Step.FindFlower, $"查找{flower}");
                        break;
                    case Step.FindFlower:
                        if (await Station() is not { } header || await Empty() == null) break;
                        // 从空选开始。保留顶部空间让选中后的减号完整可见。
                        var card = await Find($"flowers/{flower}", 110, 139, 734, 1003, .988);
                        if (card != null)
                        {
                            // 这次点击计入数量，不能照原样补发；没选中时由下一步发现并回到这里。
                            if (await Add(card.X + card.Width / 2d, card.Y + card.Height / 2d))
                                loop.Advance(Step.Quantity, $"选择{flower}，目标 {quantity} 朵", retryable: false);
                            break;
                        }
                        if (loop.StepElapsed.TotalSeconds > 20)
                        {
                            outcome = $"跳过：列表中未找到{flower}";
                            if (await loop.KeyAsync("Esc")) loop.Advance(Step.Exit, "退出农作物出售站");
                            break;
                        }
                        await loop.ScrollAsync(screen.ClientX(header.X + 300 * screen.Scale),
                            screen.ClientY(header.Y + 550 * screen.Scale), FlowerScrollDelta);
                        break;
                    case Step.Quantity:
                        if (await Station() == null) break;
                        // 右侧仍是空选：选花的点击没有生效，重新选。
                        if (remaining > 0 && loop.InputIdleFor((int)TaskPacing.PointerRetryDelay.TotalMilliseconds)
                            && await Empty() != null)
                        {
                            logger?.LogWarning("自动出售：{Flower}没有选中，重新选择", flower);
                            remaining = quantity;
                            loop.Advance(Step.FindFlower, $"查找{flower}");
                            break;
                        }
                        if (await Find("sell", 90, 1020, 390, 1130, .98) is not { } sell) break;
                        if (remaining > 0)
                        {
                            // 收入栏出现后卡片可能移位；校准一次，在同一点连点，失焦后再定位。
                            if (await Find("minus", 98, 104, 740, 967, .965) is not { } minus) break;
                            var cx = minus.X + minus.Width / 2d + 82 * screen.Scale;
                            var cy = minus.Y + minus.Height / 2d + 71 * screen.Scale;
                            while (remaining > 0)
                                if (!await Add(cx, cy)) break;
                            break;
                        }
                        var income = await ReadIncomeAsync(frame, screen, ocr, token);
                        var price = income is null ? "预期收入未识别" : $"预期收入 {income} 金币";
                        outcome = $"成功：已领取{flower}的出售奖励，{price}";
                        if (await loop.ClickAsync(screen, sell)) loop.Advance(Step.Confirm, "等待出售确认");
                        break;
                    case Step.Confirm:
                        if (await Find("confirm-title", 755, 360, 1280, 508) != null &&
                            await Find("confirm", 1030, 890, 1340, 1000, .98) is { } confirm &&
                            await loop.ClickAsync(screen, confirm))
                            loop.Advance(Step.Reward, "等待出售奖励");
                        break;
                    case Step.Reward:
                        // 确认框消失即视为已成交：之后即使卡在奖励界面，也不能重跑再卖一次。
                        if (await Find("confirm-title", 755, 360, 1280, 508) == null) loop.CommittedOutcome = outcome;
                        if (await Find("reward", 820, 370, 1240, 540) != null)
                        {
                            loop.CommittedOutcome = outcome;
                            if (await loop.KeyAsync("Esc")) loop.Advance(Step.Exit, "退出农作物出售站");
                        }
                        break;
                    case Step.Exit:
                        if (await screen.World(token) != null) return outcome;
                        if (await Station() != null || await Dialogue() != null) await loop.RepeatKeyAsync("Esc");
                        break;
                }

                return null;
            });
            if (result is not null) return result;
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
            var result = await ocr.RecognizeAsync(frame, digits, TextRecognitionMethod.OnnxOcr, token);
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
