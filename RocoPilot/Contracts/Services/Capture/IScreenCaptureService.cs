using RocoPilot.Models.Capture;

namespace RocoPilot.Contracts.Services.Capture;

public interface IScreenCaptureService
{
    CapturedFrame? Capture(CaptureTargetWindow targetWindow, CaptureMethod method);
}
