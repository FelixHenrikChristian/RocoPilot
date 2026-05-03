using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

public interface ITextRecognitionBackend
{
    TextRecognitionMethod Method
    {
        get;
    }

    TextRecognitionMethodOption GetOption();

    Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken);
}
