using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IInfoOverlayService
{
    void Show(RuntimeTaskState state);

    void Hide();

    void ResetPosition();

    void SetLocked(bool isLocked);

    void UpdateSnapshot(InfoOverlaySnapshot snapshot);
}
