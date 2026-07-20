using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class OnnxOcrV5SingleLineTextRecognitionTestBackend
{
    private const string MethodName = "ONNX Runtime PP-OCRv5（单行）";
    private const string Description = "使用 ONNX Runtime PP-OCRv5 直接识别已裁好的单行文字；不适用于整图或多行文字。";

    private readonly OnnxOcrV5SingleLineTextRecognitionBackend _singleLineBackend;

    public OnnxOcrV5SingleLineTextRecognitionTestBackend(
        OnnxOcrV5SingleLineTextRecognitionBackend singleLineBackend)
    {
        _singleLineBackend = singleLineBackend;
    }

    public TextRecognitionMethodOption GetOption()
    {
        return _singleLineBackend.IsAvailable
            ? new TextRecognitionMethodOption(
                TextRecognitionMethod.OnnxOcrV5,
                "ONNX OCR v5（单行加速）",
                Description,
                true)
            : new TextRecognitionMethodOption(
                TextRecognitionMethod.OnnxOcrV5,
                "ONNX OCR v5（单行加速）",
                Description,
                false,
                "ONNX OCR 模型或运行时不可用。");
    }

    public async Task<TextRecognitionResult> RecognizeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        var result = await _singleLineBackend.RecognizeAsync(imageBytes, cancellationToken);
        return new TextRecognitionResult(
            TextRecognitionMethod.OnnxOcrV5,
            MethodName,
            result.LanguageName,
            result.Lines,
            result.WordCount);
    }
}
