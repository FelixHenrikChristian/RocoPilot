using System.Diagnostics;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

/// <summary>
/// 自动丢球：仅在大世界场景，按设定间隔在当前鼠标位置按住左键后松开。
/// 按住时长与两次丢球的间隔均在配置的随机范围内取值（默认按住 0.8~1.5 秒、间隔 1~1.5 秒），
/// 无限循环直到手动停止。首次进入大世界时先按一次 E 切换到精灵球再开始丢球。
/// 切出游戏或长时间离开大世界场景时自动暂停，恢复后继续。
/// </summary>
public sealed class ThrowBallRunner(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, IMouseInputService mouse)
{
    public async Task<string> RunAsync(
        RuntimeTaskState state,
        IndependentTaskSettings settings,
        Action<IndependentTaskProgress> progress,
        CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        keyboard.EnsureReady(input.KeyboardInputMethod);
        var thrown = 0;
        var switchedToBall = false;
        var durationLimit = TimeSpan.FromMinutes(settings.ThrowBallDurationMinutes);
        var timer = Stopwatch.StartNew();
        progress(new("自动丢球", "等待大世界"));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (durationLimit > TimeSpan.Zero && timer.Elapsed >= durationLimit)
            {
                var elapsedMinutes = (int)Math.Round(timer.Elapsed.TotalMinutes);
                progress(new("自动丢球", "任务时长已到"));
                return $"成功：已丢球 {thrown} 次，共 {elapsedMinutes} 分钟";
            }
            if (!keyboard.IsWindowAvailable(window.Hwnd))
                throw new InvalidOperationException("游戏窗口已关闭。");
            if (!keyboard.IsWindowForeground(window.Hwnd))
            {
                progress(new("自动丢球", "已暂停，等待切回游戏"));
                await Task.Delay(500, token);
                continue;
            }

            if (!await IsWorldSceneAsync(state, token))
            {
                progress(new("自动丢球", "已暂停，等待回到大世界"));
                await Task.Delay(500, token);
                continue;
            }

            if (!switchedToBall)
            {
                progress(new("自动丢球", "按 E 切换到精灵球"));
                await keyboard.SendSequenceAsync(window.Hwnd, "E",
                    new() { Method = input.KeyboardInputMethod, HoldDurationMs = input.KeyboardHoldDurationMs }, token);
                switchedToBall = true;
                await Task.Delay(800, token);
            }

            // 按住时长与间隔都在配置范围内随机，避免固定节奏被检测。
            var holdMs = Random.Shared.Next(settings.ThrowBallHoldMinMs, settings.ThrowBallHoldMaxMs + 1);
            progress(new("自动丢球", $"第 {thrown + 1} 次丢球，按住 {holdMs / 1000d:0.0##} 秒"));
            await mouse.PressAtCurrentPositionAsync(window.Hwnd, holdMs, input.KeyboardInputMethod, token);
            thrown++;
            var intervalMs = Random.Shared.Next(settings.ThrowBallIntervalMinMs, settings.ThrowBallIntervalMaxMs + 1);
            progress(new("自动丢球", $"已丢球 {thrown} 次，{intervalMs / 1000d:0.0##} 秒后继续"));
            await Task.Delay(intervalMs, token);
        }
    }

    /// <summary>截图识别魔法点图标，判定当前是否处于大世界场景。</summary>
    private async Task<bool> IsWorldSceneAsync(RuntimeTaskState state, CancellationToken token)
    {
        using var frame = capture.Capture(state.TargetWindow, state.Options.CaptureMethod);
        if (frame is null)
            return false;
        var screen = new DailyTaskScreen(frame, state.TargetWindow, images);
        return await screen.World(token) is not null;
    }
}
