using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.Recognition;
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
            .OrderBy(option => option.Method == TextRecognitionDefaults.Method ? 0 : 1)
            .ToList();
    }

    public TextRecognitionMethodOption? GetDefaultMethod()
    {
        return _backends.TryGetValue(TextRecognitionDefaults.Method, out var backend)
            ? backend.GetOption()
            : null;
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

    public async Task<TextRecognitionResult> RecognizeAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        TextRecognitionMethod method,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);

        if (!_backends.TryGetValue(method, out var backend))
        {
            throw new NotSupportedException($"Unsupported text recognition method: {method}");
        }

        if (backend is IFrameTextRecognitionBackend frameBackend)
        {
            return await frameBackend.RecognizeAsync(frame, region, cancellationToken);
        }

        var imageBytes = await RecognitionRegionImageHelper.EncodePngAsync(
            frame,
            region,
            cancellationToken);
        return await backend.RecognizeAsync(imageBytes, cancellationToken);
    }
}
