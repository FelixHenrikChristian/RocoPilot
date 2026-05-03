using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IRecognitionOverlayService
{
    void Show(RuntimeTaskState state);

    void Hide();
}
