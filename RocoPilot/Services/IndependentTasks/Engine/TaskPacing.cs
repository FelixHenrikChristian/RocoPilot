namespace RocoPilot.Services.IndependentTasks.Engine;

/// <summary>
/// 独立任务的统一节奏。任务里不再各写各的固定等待：目标稳定出现一段时间后才出手，
/// 出手后由下一步的画面确认结果，没生效再补一次。要调快慢只改这里。
/// </summary>
internal static class TaskPacing
{
    /// <summary>两次识别之间的间隔。</summary>
    public const int PollMs = 150;

    /// <summary>
    /// 反应时间：目标要连续可见、位置不动满这么久才出手。
    /// 界面刚出来时控件往往还在滑入或淡入，这时候的点击和按键游戏不认。
    /// </summary>
    public const int ReactionMs = 500;

    /// <summary>两次识别到的点击位置相差不超过这么多像素，才算目标没有在动。</summary>
    public const int StablePixels = 6;

    /// <summary>
    /// 目标一直在动（地图上跳动的标记）时最多等这么久，之后按最新位置出手，不无限等它停下。
    /// </summary>
    public const int MovingTargetMs = 2000;

    /// <summary>目标短暂识别不到（图标闪烁、得分在阈值上下）不超过这么久，不重新计反应时间。</summary>
    public const int SightingGraceMs = 400;

    /// <summary>输入之后至少等这么久再识别，避免把输入前的画面当成结果。</summary>
    public const int SettleMs = 300;

    /// <summary>
    /// 输入之后这么久仍没有新画面，就接受旧画面：静止界面不产生新帧，
    /// 一直等新帧会把“点击没生效”拖成超时。
    /// </summary>
    public const int StaleFrameAfterMs = 700;

    /// <summary>同一界面上反复按同一个键（Esc 逐层退出）的最小间隔。</summary>
    public const int RepeatKeyMs = 1500;

    /// <summary>退出阶段画面一直认不出来（被奖励弹层、提示挡住）满这么久，按一次 Esc 试着关掉。</summary>
    public const int UnknownScreenEscapeMs = 3000;

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
