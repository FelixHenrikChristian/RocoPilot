namespace RocoPilot.Configuration;

/// <summary>
/// 实时任务的截图与识别间隔，单位均为毫秒；统一在此调整，修改后重新编译。
/// 间隔越小，响应越快、资源占用越高；间隔越大，场景和战斗信息更新越慢。
/// </summary>
internal static class RuntimeRecognitionDefaults
{
    // 截图循环的目标周期：100 ms，约 10 帧/秒。
    // 已扣除本轮截图和场景处理耗时；处理超时则至少等待 1 ms，不补跑积压轮次。
    public const int FrameCaptureIntervalMs = 100;

    // 场景及战斗状态的识别间隔：500 ms，约 2 次/秒；同时决定自动战斗的状态响应速度。
    // 复用截图循环的画面，只在采集轮次检查；实际间隔也受截图和状态处理耗时影响。
    // 调整时应保持不小于截图周期，避免提高频率却无法取得更新的画面。
    public const int GameStateScanIntervalMs = 500;

    // 后台周期性 OCR 的检查间隔：1000 ms，约每秒尝试一次，用于战斗提示和精灵名等文字。
    // 仅处理已确认的战斗画面；上一轮 OCR 未完成时跳过，避免并发堆积。
    // 增大会延迟异色等战斗信息的识别；此值不控制启动 UID 等按需执行的 OCR。
    public const int OcrScanIntervalMs = 1000;
}
