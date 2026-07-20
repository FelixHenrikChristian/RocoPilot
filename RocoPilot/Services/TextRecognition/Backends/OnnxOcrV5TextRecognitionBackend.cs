using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class OnnxOcrV5TextRecognitionBackend : ITextRecognitionBackend, IFrameTextRecognitionBackend
{
    private const string MethodName = "ONNX Runtime PP-OCRv5";
    private const string Description = "使用 ONNX Runtime PP-OCRv5 直接识别游戏 OCR 区域。";

    private readonly OnnxOcrV5SingleLineTextRecognitionBackend _recognizer;

    public OnnxOcrV5TextRecognitionBackend(OnnxOcrV5SingleLineTextRecognitionBackend recognizer)
    {
        _recognizer = recognizer;
    }

    public TextRecognitionMethod Method => TextRecognitionMethod.OnnxOcrV5;

    public TextRecognitionMethodOption GetOption()
    {
        return _recognizer.IsAvailable
            ? new TextRecognitionMethodOption(Method, "ONNX OCR v5", Description, true)
            : new TextRecognitionMethodOption(
                Method,
                "ONNX OCR v5",
                Description,
                false,
                "ONNX OCR 模型或运行时不可用。");
    }

    public Task<TextRecognitionResult> RecognizeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        return _recognizer.RecognizeAsync(imageBytes, cancellationToken);
    }

    public Task<TextRecognitionResult> RecognizeAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        CancellationToken cancellationToken)
    {
        return _recognizer.RecognizeAsync(frame, region, cancellationToken);
    }
}
