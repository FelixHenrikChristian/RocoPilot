using RocoPilot.Models.Capture;

namespace RocoPilot.Contracts.Services;

public interface IGameWindowService
{
    string TargetProcessName
    {
        get;
    }

    CaptureTargetWindow? FindGameWindow();

    bool TryBringGameWindowToForeground(CaptureTargetWindow window);
}
