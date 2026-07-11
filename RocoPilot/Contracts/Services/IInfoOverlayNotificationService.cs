using RocoPilot.Models.Overlay;

namespace RocoPilot.Contracts.Services;

public interface IInfoOverlayNotificationService
{
    void UpdateUidNotice(InfoOverlayNotice? notice);
}
