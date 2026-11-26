using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class OnnxOcrTextRecognitionBackend : ITextRecognitionBackend, IFrameTextRecognitionBackend
{
    private const string DisplayName = "ONNX OCR v6 small";
    private const string Description = "使用 PP-OCRv6 small 识别游戏画面中的文字。";

    private readonly OnnxOcrSingleLineTextRecognitionBackend _recognizer;

    public OnnxOcrTextRecognitionBackend(OnnxOcrSingleLineTextRecognitionBackend recognizer)
    {
        _recognizer = recognizer;
    }

    public TextRecognitionMethod Method => TextRecognitionMethod.OnnxOcr;

    public TextRecognitionMethodOption GetOption()
    {
        return _recognizer.IsAvailable
            ? new TextRecognitionMethodOption(Method, DisplayName, Description, true)
            : new TextRecognitionMethodOption(
                Method,
                DisplayName,
                Description,
                false,
                "ONNX OCR 模型或运行时不可用，请检查应用文件是否完整，或重新安装 RocoPilot。");
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
