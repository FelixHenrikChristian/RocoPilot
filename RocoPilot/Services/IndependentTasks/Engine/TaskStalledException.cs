namespace RocoPilot.Services.IndependentTasks.Engine;

/// <summary>
/// 任务卡住了：步骤内的补发用完、等到超时，画面仍没有按预期变化。
/// 这类失败可以恢复，调度层会退回大世界再跑一次；
/// 目标不存在、印记不足、窗口已关闭等重跑也没用的情况仍用 <see cref="InvalidOperationException"/>。
/// </summary>
public sealed class TaskStalledException(string message, Exception? innerException = null)
    : TimeoutException(message, innerException)
{
    /// <summary>
    /// 不可逆的操作（出售、制造、领取）已经完成，只是没能正常退出界面。
    /// 调度层退回大世界后直接采用这个结果，不再重跑。
    /// </summary>
    public string? CommittedOutcome { get; init; }

    /// <summary>卡住之前已经成功的次数（挑战类任务），重跑时只补剩下的。</summary>
    public int CompletedRuns { get; init; }
}
