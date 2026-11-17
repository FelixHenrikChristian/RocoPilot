using RocoPilot.Models.Capture;

namespace RocoPilot.Contracts.Services;

/// <summary>挑战结束后取走最后一次“挑战结果”画面，给完成通知配图。取走后由调用方释放。</summary>
public interface IChallengeResultFrameSource
{
    CapturedFrame? TakeResultFrame();
}
