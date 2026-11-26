using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.IndependentTasks.Engine;

namespace RocoPilot.Services.IndependentTasks;

public sealed class EveningRestRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse, ITextRecognitionService ocr,
    ILogger<EveningRestRunner>? logger = null)
{
    private enum Step { Open, Explore, CheckEvent, Teleport, Arrival, Monitor, Exit, WishMenu, ReturnToWorld }

    public async Task<string> RunAsync(RuntimeTaskState state, Action<IndependentTaskProgress> progress, CancellationToken token)
    {
        var window = state.TargetWindow;
        var arrived = false;
        var wishClicks = 0;
        var lastWishInput = DateTimeOffset.MinValue;
        var exitReason = "";
        string? starlight = null;
        var loop = new TaskLoop<Step>("晚风小憩", state, capture, keyboard, mouse, progress,
            step => Describe(step, null), Step.Open, token, logger)
        {
            Timeout = _ => TimeSpan.FromSeconds(60),
            Poll = step => step == Step.Monitor ? 5000 : TaskPacing.PollMs
        };
        while (true)
        {
            using var frame = await loop.NextFrameAsync();
            if (frame is null) continue;
            var screen = new DailyTaskScreen(frame, window, images);
            Task<ImageMatchResult?> Find(string name, int left, int top, int right, int bottom) =>
                screen.Find($"evening-rest/{name}", left, top, right, bottom, token);
            Task<ImageMatchResult?> Explore() => Find("explore", 175, 34, 310, 105);
            // 晚间活动会更换标题，共用选中项的月亮图标。
            Task<ImageMatchResult?> Event() => Find("event", 85, 118, 200, 1000);
            async Task<string?> ReadStarlight()
            {
                // 进度居中，末位固定是白色的 0。按右侧留白对称收窄，排除短读数左侧的水晶图标。
                var region = screen.Region(1751, 848, 1921, 879);
                var middleRow = (region.Y + region.Height / 2) * frame.Width;
                var margin = 0;
                while (margin < region.Width / 2)
                {
                    var pixel = (middleRow + region.X + region.Width - 1 - margin) * 4;
                    if (frame.Pixels[pixel] > 150 && frame.Pixels[pixel + 1] > 150 && frame.Pixels[pixel + 2] > 150) break;
                    margin++;
                }
                if (margin == region.Width / 2) return null;
                var trim = Math.Max(0, margin - (int)Math.Ceiling(6 * screen.Scale));
                region.X += trim;
                region.Width -= 2 * trim;
                var result = await ocr.RecognizeAsync(frame, region, TextRecognitionMethod.OnnxOcrV5, token);
                var text = result.Text.Replace(" ", "").Trim();
                if (!text.EndsWith("/1200", StringComparison.Ordinal)) return null;
                return int.TryParse(text.AsSpan(0, text.Length - 5), out var value) &&
                    value is >= 0 and <= 1200 && text == $"{value}/1200" ? text : null;
            }

            void Leave(string reason)
            {
                exitReason = reason;
                loop.Advance(Step.Exit, reason);
            }

            var result = await loop.RunStepAsync(async () =>
            {
                switch (loop.Step)
                {
                    case Step.Open:
                    case Step.Arrival:
                        if (await screen.World(token) is not null)
                        {
                            var arriving = loop.Step == Step.Arrival;
                            if (!await loop.KeyAsync("F2")) break;
                            arrived |= arriving;
                            loop.Advance(Step.Explore);
                        }
                        else if (loop.Step == Step.Open && (await Explore() is not null ||
                            await Find("tab", 300, 1025, 470, 1140) is not null)) loop.Advance(Step.Explore);
                        break;
                    case Step.Explore:
                        if (await Explore() is not null) loop.Advance(Step.CheckEvent);
                        else if (await Find("tab", 300, 1025, 470, 1140) is { } tab && loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                            await loop.ClickAsync(screen, tab);
                        break;
                    case Step.CheckEvent:
                    case Step.Monitor:
                        if (await Explore() is null) break;
                        if (await Event() is null)
                        {
                            if (await Find("event-unselected", 85, 118, 200, 1000) is { } activity)
                            {
                                if (loop.InputIdleFor(TaskPacing.RepeatKeyMs)) await loop.ClickAsync(screen, activity);
                                break;
                            }
                            if (arrived || loop.StepElapsed.TotalSeconds < 3) break;
                            Leave("跳过：未找到晚间休憩活动");
                            break;
                        }
                        var previousStarlight = starlight;
                        starlight = await ReadStarlight() ?? starlight;
                        if (starlight == "1200/1200") Leave("成功：本日星光已达 1200/1200");
                        else if (arrived)
                        {
                            // 有效活动页可以长时间停留；只对界面丢失使用导航超时。
                            if (loop.Step != Step.Monitor) loop.Advance(Step.Monitor, Describe(Step.Monitor, starlight), stageLabel: "收集星光");
                            else
                            {
                                loop.KeepAlive();
                                if (starlight != previousStarlight) loop.Report(Describe(Step.Monitor, starlight));
                            }
                        }
                        else if (await Find("go", 1470, 995, 1620, 1130) is { } go)
                        {
                            if (await loop.ClickAsync(screen, go)) loop.Advance(Step.Teleport);
                        }
                        else if (loop.StepElapsed.TotalSeconds >= 5) Leave("跳过：未找到前往按钮");
                        break;
                    case Step.Teleport:
                        if (await screen.Map(token) is not null &&
                            await screen.Teleport(token) is { } teleport &&
                            await loop.ClickAsync(screen, teleport))
                            loop.Advance(Step.Arrival);
                        break;
                    case Step.Exit:
                        if (await screen.World(token) is not null)
                        {
                            if (starlight != "1200/1200") return exitReason;
                            if (await loop.RepeatKeyAsync("Esc")) loop.Advance(Step.WishMenu);
                            break;
                        }
                        if (await Explore() is not null) await loop.RepeatKeyAsync("Esc");
                        break;
                    case Step.WishMenu:
                        if (!loop.InputIdleFor(500)) break;
                        if (frame.CapturedAt <= lastWishInput)
                        {
                            // 静止界面可能持续返回点击前的旧帧；没有新画面就结束补点。
                            if (loop.InputIdleFor(TaskPacing.RepeatKeyMs))
                                loop.Advance(Step.ReturnToWorld, retryable: false);
                            break;
                        }
                        var point = await LocateWishAsync(screen, frame, token);
                        if (point is null)
                        {
                            if (wishClicks > 0) loop.Advance(Step.ReturnToWorld, retryable: false);
                            break;
                        }
                        if (await loop.ClickAsync(point.Value.X, point.Value.Y))
                        {
                            wishClicks++;
                            lastWishInput = DateTimeOffset.UtcNow;
                            loop.CommittedOutcome = exitReason + "，已尝试领取许愿星";
                            // 首次点击后解除开菜单按键的自动补发，避免点击后又打开菜单。
                            loop.Advance(wishClicks == 3 ? Step.ReturnToWorld : Step.WishMenu, retryable: false);
                        }
                        break;
                    case Step.ReturnToWorld:
                        if (await screen.World(token) is not null) return loop.CommittedOutcome;
                        await loop.RepeatKeyAsync("Esc");
                        break;
                }

                return null;
            });
            if (result is not null) return result;
        }
    }

    internal static async Task<(int X, int Y)?> LocateWishAsync(DailyTaskScreen screen, CapturedFrame frame, CancellationToken token)
    {
        var quest = await screen.Find("evening-rest/menu-quest", 985, 155, 1180, 305, token, .90, anchor: RecognitionAnchor.Center);
        if (quest is null) return null;
        var bag = await screen.Find("evening-rest/menu-bag", 985, 770, 1180, 940, token, .90, anchor: RecognitionAnchor.Center);
        if (bag is null) return null;
        var x = (int)Math.Round((quest.X + bag.X + (quest.Width + bag.Width) / 2d) / 2);
        var y = (int)Math.Round((quest.Y + bag.Y + (quest.Height + bag.Height) / 2d) / 2);

        // 弹窗可能只挡住中央。检查中点附近是否仍有一小片浅金色，容许星核浮动。
        var radius = Math.Max(1, (int)Math.Round(12 * screen.Scale));
        var gold = 0;
        for (var row = Math.Max(0, y - radius); row <= Math.Min(frame.Height - 1, y + radius); row++)
        {
            for (var col = Math.Max(0, x - radius); col <= Math.Min(frame.Width - 1, x + radius); col++)
            {
                var i = (row * frame.Width + col) * 4;
                var red = frame.Pixels[i + 2];
                if (red > 235 && frame.Pixels[i + 1] > 210 && red - frame.Pixels[i] > 20) gold++;
            }
        }
        return gold >= radius * radius / 4 + 1 ? (screen.ClientX(x), screen.ClientY(y)) : null;
    }

    private static string Describe(Step step, string? starlight) => step switch
    {
        Step.Open or Step.Explore => "打开活动探索页",
        Step.CheckEvent => "选择晚间休憩活动",
        Step.Teleport => "传送到活动地点",
        Step.Arrival => "等待传送完成",
        Step.Monitor => starlight is null ? "等待识别本日星光" : $"本日星光 {starlight}",
        Step.WishMenu => "定位并尝试领取许愿星",
        _ => "返回大世界"
    };
}
