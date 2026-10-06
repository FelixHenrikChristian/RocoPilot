using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Contracts.Services;

public interface IRecognitionOverlayService
{
    void Show(RuntimeTaskState state, RecognitionRegionConfig? regionConfig = null);

    void ShowOcrResult(string regionId, string text);

    void ShowImageMatchResult(string regionId, double score);

    void Hide();
}
