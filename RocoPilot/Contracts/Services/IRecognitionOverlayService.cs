using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IRecognitionOverlayService
{
    void Show(RuntimeTaskState state);

    void ShowOcrResult(string regionId, string text);

    void ShowImageMatchResult(string regionId, double score);

    void Hide();
}
