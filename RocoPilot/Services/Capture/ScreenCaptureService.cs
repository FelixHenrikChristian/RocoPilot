using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Services.Capture.Backends;

namespace RocoPilot.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly IReadOnlyDictionary<CaptureMethod, ICaptureBackend> _captureBackends;

    public ScreenCaptureService(IEnumerable<ICaptureBackend> captureBackends)
    {
        _captureBackends = captureBackends.ToDictionary(backend => backend.Method);
    }

    public CapturedFrame? Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
    {
        if (targetWindow.Hwnd == IntPtr.Zero || !_captureBackends.TryGetValue(method, out var captureBackend))
        {
            return null;
        }

        return captureBackend.Capture(targetWindow);
    }

    public void Release(CaptureTargetWindow targetWindow, CaptureMethod method)
    {
        if (targetWindow.Hwnd == IntPtr.Zero || !_captureBackends.TryGetValue(method, out var captureBackend))
        {
            return;
        }

        captureBackend.Release(targetWindow);
    }
}
