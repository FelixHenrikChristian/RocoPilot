using OpenCvSharp;

using RocoPilot.Models.TextRecognition;

using Tesseract;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class TesseractTextRecognitionBackend : ITextRecognitionBackend, IDisposable
{
    private const string MethodName = "Tesseract OCR 5";
    private const string LanguageName = "简体中文/英文";
    private const string LanguageCode = "chi_sim+eng";

    private readonly Lazy<TesseractEngine> _engine = new(CreateEngine);
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private bool _isDisposed;

    public TextRecognitionMethod Method => TextRecognitionMethod.TesseractOcr;

    public TextRecognitionMethodOption GetOption()
    {
        var missingLanguageData = GetMissingLanguageDataFiles().ToList();
        if (missingLanguageData.Count > 0)
        {
            return new TextRecognitionMethodOption(
                Method,
                MethodName,
                "使用 Tesseract 5 本地引擎识别中英文文字",
                isAvailable: false,
                unavailableReason: $"缺少语言数据：{string.Join(", ", missingLanguageData)}");
        }

        return new TextRecognitionMethodOption(
            Method,
            MethodName,
            "使用 Tesseract 5 本地引擎识别中英文文字，适合作为离线兜底方案",
            isAvailable: true);
    }

    public async Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            var pngBytes = EncodeForTesseract(imageBytes);
            using var pix = Pix.LoadFromMemory(pngBytes);
            using var page = await Task.Run(() => _engine.Value.Process(pix), cancellationToken);

            return TextRecognitionResultFactory.Create(
                Method,
                MethodName,
                LanguageName,
                page.GetText().Trim());
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    private static TesseractEngine CreateEngine()
    {
        var missingLanguageData = GetMissingLanguageDataFiles().ToList();
        if (missingLanguageData.Count > 0)
        {
            throw new InvalidOperationException($"缺少 Tesseract 语言数据：{string.Join(", ", missingLanguageData)}");
        }

        var engine = new TesseractEngine(GetTessDataPath(), LanguageCode, EngineMode.Default);
        engine.SetVariable("user_defined_dpi", "300");
        return engine;
    }

    private static byte[] EncodeForTesseract(byte[] imageBytes)
    {
        using var source = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException("无法解码图像内容。");
        }

        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var scaled = new Mat();
        Cv2.Resize(gray, scaled, default, 2, 2, InterpolationFlags.Cubic);

        using var binary = new Mat();
        Cv2.Threshold(scaled, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        if (Cv2.CountNonZero(binary) < binary.Rows * binary.Cols / 2)
        {
            Cv2.BitwiseNot(binary, binary);
        }

        Cv2.ImEncode(".png", binary, out var encoded);
        return encoded;
    }

    private static IEnumerable<string> GetMissingLanguageDataFiles()
    {
        var tessDataPath = GetTessDataPath();
        foreach (var fileName in new[] { "chi_sim.traineddata", "eng.traineddata" })
        {
            if (!File.Exists(Path.Combine(tessDataPath, fileName)))
            {
                yield return fileName;
            }
        }
    }

    private static string GetTessDataPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "OCR", "TesseractOCR");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_engine.IsValueCreated)
        {
            _engine.Value.Dispose();
        }

        _recognitionLock.Dispose();
        _isDisposed = true;
    }
}
