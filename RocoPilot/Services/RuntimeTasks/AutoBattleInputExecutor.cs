using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;
using RocoPilot.Services.Logging;

namespace RocoPilot.Services.RuntimeTasks;

/// <summary>执行已确定的按键计划，不维护回合或决定战斗策略。</summary>
public sealed class AutoBattleInputExecutor(IKeyboardInputService keyboard, ILogger<AutoBattleInputExecutor> logger)
{
    // 配置里的序列写错时每一轮扫描都会走到这里，同一种故障不逐次记录。
    private readonly RepeatedFailureGate _invalidSequences = new(TimeSpan.FromSeconds(30));

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
            var report = _invalidSequences.TryEnter(out var skipped);
            if (plan.FallbackSequence is null
                || !keyboard.TryParseSequence(plan.FallbackSequence, out strokes, out _)
                || strokes.Count == 0)
            {
                if (report)
                {
                    logger.LogError("自动战斗：按键序列“{Sequence}”无法识别，这一步没有执行，请检查战斗配置（{Error}）{Repeat}",
                        plan.Sequence, error, RepeatedFailureGate.Describe(skipped));
                }

                return false;
            }

            if (report)
            {
                logger.LogWarning("自动战斗：按键序列“{Sequence}”无法识别，已改用技能键 {Fallback}（{Error}）{Repeat}",
                    plan.Sequence, plan.FallbackSequence, error, RepeatedFailureGate.Describe(skipped));
            }

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
            catch (Exception ex) { logger.LogWarning(ex, "更新自动战斗展示信息失败，按键照常发送"); }
        }
    }

    public static KeyboardInputOptions CreateOptions(AutoBattleSettings settings, bool capture = false) => new()
    {
        Method = settings.KeyboardInputMethod,
        HoldDurationMs = settings.KeyboardHoldDurationMs,
        IntervalMs = capture ? settings.CaptureKeyboardIntervalMs : settings.KeyboardIntervalMs
    };
}
