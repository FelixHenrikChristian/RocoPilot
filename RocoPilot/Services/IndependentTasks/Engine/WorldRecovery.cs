using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks.Engine;

/// <summary>
/// 任务卡住后的统一收场：按 Esc 逐层退出，直到看见大世界 HUD。
/// 每个任务都从大世界开始，所以退回大世界之后既可以重跑这个任务，也可以接着跑清单里的下一个。
/// </summary>
public sealed class WorldRecovery(IScreenCaptureService capture, IImageMatchingService images,
    IKeyboardInputService keyboard, ILogger<WorldRecovery>? logger = null)
{
    private const int MaximumEscapes = 8;
    private static readonly TimeSpan EscapeInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(40);

    /// <returns>是否已回到大世界。窗口关闭、长时间不在前台、退不出去（例如战斗中、掉线弹窗）时返回 false。</returns>
    public async Task<bool> ReturnToWorldAsync(RuntimeTaskState state, CancellationToken token)
    {
        var window = state.TargetWindow;
        var input = state.Options.AutoBattleSettings;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var sinceEscape = System.Diagnostics.Stopwatch.StartNew();
        var escapes = 0;
        var seen = 0;
        while (started.Elapsed < Limit)
        {
            await Task.Delay(TaskPacing.SettleMs, token);
            if (!keyboard.IsWindowAvailable(window.Hwnd)) return false;
            // 不在前台时不发按键，也不替用户切窗口；在时限内等用户切回来。
            if (!keyboard.IsWindowForeground(window.Hwnd)) continue;
            using var frame = await Task.Run(() => capture.Capture(window, state.Options.CaptureMethod), token);
            if (frame is null) continue;
            if (await new DailyTaskScreen(frame, window, images).World(token) is not null)
            {
                // 连续两次看到才算数，避免把界面切换中的一帧当成已经回到大世界。
                if (++seen >= 2)
                {
                    logger?.LogInformation("已退回大世界（按了 {Count} 次 Esc）", escapes);
                    return true;
                }

                continue;
            }

            seen = 0;
            if (escapes > 0 && sinceEscape.Elapsed < EscapeInterval) continue;
            if (escapes >= MaximumEscapes) break;
            try
            {
                await keyboard.SendSequenceAsync(window.Hwnd, "Esc",
                    new() { Method = input.KeyboardInputMethod, HoldDurationMs = input.KeyboardHoldDurationMs }, token);
            }
            catch (InvalidOperationException ex)
            {
                logger?.LogDebug(ex, "退回大世界时 Esc 没有发出去，稍后重试");
                continue;
            }

            escapes++;
            sinceEscape.Restart();
        }

        logger?.LogWarning("没能退回大世界：按了 {Count} 次 Esc 后仍未看到大世界界面", escapes);
        return false;
    }
}
