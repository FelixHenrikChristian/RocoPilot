using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

public interface ISingleLineTextRecognitionBackend
{
    TextRecognitionMethod Method
    {
        get;
    }

    bool IsAvailable
    {
        get;
    }

    Task<TextRecognitionResult> RecognizeAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        CancellationToken cancellationToken);
}
