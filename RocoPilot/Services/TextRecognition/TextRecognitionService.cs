using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Services.TextRecognition;

public sealed class TextRecognitionService : ITextRecognitionService
{
    private readonly IReadOnlyDictionary<TextRecognitionMethod, ITextRecognitionBackend> _backends;

    public TextRecognitionService(IEnumerable<ITextRecognitionBackend> backends)
    {
        _backends = backends.ToDictionary(backend => backend.Method);
    }

    public IReadOnlyList<TextRecognitionMethodOption> GetMethods()
    {
        return _backends.Values
            .Select(backend => backend.GetOption())
            .ToList();
    }

    public Task<TextRecognitionResult> RecognizeAsync(
        byte[] imageBytes,
        TextRecognitionMethod method,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("图像内容为空。", nameof(imageBytes));
        }

        if (!_backends.TryGetValue(method, out var backend))
        {
            throw new NotSupportedException($"不支持的文字识别方法：{method}");
        }

        return backend.RecognizeAsync(imageBytes, cancellationToken);
    }
}
