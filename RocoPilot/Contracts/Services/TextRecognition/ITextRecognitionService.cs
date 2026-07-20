using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Contracts.Services.TextRecognition;

public interface ITextRecognitionService
{
    IReadOnlyList<TextRecognitionMethodOption> GetMethods();

    TextRecognitionMethodOption? GetDefaultMethod();

    Task<TextRecognitionResult> RecognizeAsync(
        byte[] imageBytes,
        TextRecognitionMethod method,
        CancellationToken cancellationToken = default);

    Task<TextRecognitionResult> RecognizeAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        TextRecognitionMethod method,
        CancellationToken cancellationToken = default);
}
