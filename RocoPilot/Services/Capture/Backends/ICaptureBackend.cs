using RocoPilot.Models.Capture;

namespace RocoPilot.Services.Capture.Backends;

public interface ICaptureBackend
{
    CaptureMethod Method
    {
        get;
    }

    CapturedFrame? Capture(CaptureTargetWindow targetWindow);

    void Release(CaptureTargetWindow targetWindow);
}
