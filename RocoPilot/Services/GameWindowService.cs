using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;

namespace RocoPilot.Services;

public sealed class GameWindowService : IGameWindowService
{
    private const string TargetProcessNameWithoutExtension = "NRC-Win64-Shipping";

    private readonly IWindowEnumerationService _windowEnumerationService;

    public string TargetProcessName => $"{TargetProcessNameWithoutExtension}.exe";

    public GameWindowService(IWindowEnumerationService windowEnumerationService)
    {
        _windowEnumerationService = windowEnumerationService;
    }

    public CaptureTargetWindow? FindGameWindow()
    {
        return _windowEnumerationService
            .GetVisibleWindows()
            .Where(IsTargetProcessWindow)
            .OrderByDescending(window => window.Width * window.Height)
            .FirstOrDefault();
    }

    private static bool IsTargetProcessWindow(CaptureTargetWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.ProcessName))
        {
            return false;
        }

        var processNameWithoutExtension = Path.GetFileNameWithoutExtension(window.ProcessName);
        return string.Equals(
            processNameWithoutExtension,
            TargetProcessNameWithoutExtension,
            StringComparison.OrdinalIgnoreCase);
    }
}
