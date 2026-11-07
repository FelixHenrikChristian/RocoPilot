using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;

namespace RocoPilot.Services.RuntimeTasks;

/// <summary>执行已确定的按键计划，不维护回合或决定战斗策略。</summary>
public sealed class AutoBattleInputExecutor(IKeyboardInputService keyboard, ILogger<AutoBattleInputExecutor> logger)
{
    public bool CanSend(nint window, AutoBattleSettings settings)
    {
        return keyboard.IsWindowAvailable(window)
            && (!keyboard.RequiresForeground(settings.KeyboardInputMethod) || keyboard.IsWindowForeground(window));
    }

    public async Task<bool> ExecuteAsync(nint window, AutoBattleSettings settings, AutoBattlePlan plan, CancellationToken token,
        Action<string, bool>? progress = null)
    {
        if (!plan.ShouldSendKeys || !CanSend(window, settings)) return false;

        var sequence = plan.Sequence;
        if (!keyboard.TryParseSequence(sequence, out var strokes, out var error) || strokes.Count == 0)
        {
            logger.LogWarning("自动战斗序列无效。Sequence={Sequence}, Error={Error}", plan.Sequence, error);
            if (plan.FallbackSequence is null
                || !keyboard.TryParseSequence(plan.FallbackSequence, out strokes, out _)
                || strokes.Count == 0) return false;
            sequence = plan.FallbackSequence;
        }

        token.ThrowIfCancellationRequested();
        ReportProgress(sequence, false);
        await keyboard.SendSequenceAsync(window, strokes, CreateOptions(settings, plan.Action == AutoBattleAction.Capture), token);
        ReportProgress(sequence, true);
        return true;

        void ReportProgress(string actualSequence, bool completed)
        {
            try { progress?.Invoke(actualSequence, completed); }
            catch (Exception ex) { logger.LogWarning(ex, "更新自动战斗展示信息失败，不影响按键执行"); }
        }
    }

    public static KeyboardInputOptions CreateOptions(AutoBattleSettings settings, bool capture = false) => new()
    {
        Method = settings.KeyboardInputMethod,
        HoldDurationMs = settings.KeyboardHoldDurationMs,
        IntervalMs = capture ? settings.CaptureKeyboardIntervalMs : settings.KeyboardIntervalMs
    };
}
