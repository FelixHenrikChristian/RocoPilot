namespace RocoPilot.Services.IndependentTasks.Engine;

/// <summary>
/// 独立任务的统一节奏。任务里不再各写各的固定等待：看到目标就按反应时间出手，
/// 出手后由下一步的画面确认结果，没生效再补一次。要调快慢只改这里。
/// </summary>
internal static class TaskPacing
{
    /// <summary>两次识别之间的间隔。</summary>
    public const int PollMs = 120;

    /// <summary>从看到目标到出手的反应时间，同时让按钮的出现动画播完。</summary>
    public const int ReactionMs = 200;

    /// <summary>输入之后至少等这么久再识别，避免把输入前的画面当成结果。</summary>
    public const int SettleMs = 250;

    /// <summary>
    /// 输入之后这么久仍没有新画面，就接受旧画面：静止界面不产生新帧，
    /// 一直等新帧会把“点击没生效”拖成超时。
    /// </summary>
    public const int StaleFrameAfterMs = 600;

    /// <summary>同一界面上反复按同一个键（Esc 逐层退出）的最小间隔。</summary>
    public const int RepeatKeyMs = 1200;

    /// <summary>点击、滚轮、拖动没有带来预期变化时，首次补发前的等待。</summary>
    public static readonly TimeSpan PointerRetryDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>按键多为开关键（地图、菜单），重复按会把界面关掉，补发前等得更久。</summary>
    public static readonly TimeSpan KeyRetryDelay = TimeSpan.FromMilliseconds(2500);

    /// <summary>每补发一次，下一次的等待乘以这个系数。</summary>
    public const double RetryBackoff = 1.5;

    /// <summary>同一步最多补发的次数；用完后等步骤超时，由调度层退回大世界重跑。</summary>
    public const int MaxStepRetries = 4;

    /// <summary>输入连续发不出去（鼠标被挡、驱动报错）达到这个次数就放弃本轮。</summary>
    public const int MaxInputFailures = 5;

    public static TimeSpan RetryDelay(bool pointer, int retries)
        => (pointer ? PointerRetryDelay : KeyRetryDelay) * Math.Pow(RetryBackoff, retries);
}
