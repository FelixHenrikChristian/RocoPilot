using RocoPilot.Models.Capture;

namespace RocoPilot.Contracts.Services.Capture;

public interface IWindowEnumerationService
{
    IReadOnlyList<CaptureTargetWindow> GetVisibleWindows();
}
