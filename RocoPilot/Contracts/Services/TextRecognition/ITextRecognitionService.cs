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
}
