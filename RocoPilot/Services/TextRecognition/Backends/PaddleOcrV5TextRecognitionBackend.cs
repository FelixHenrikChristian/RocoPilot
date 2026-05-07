 using System.Runtime.InteropServices;

using OpenCvSharp;

using RocoPilot.Models.TextRecognition;

using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class PaddleOcrV5TextRecognitionBackend : ITextRecognitionBackend, IDisposable
{
    private const string MethodName = "PaddleOCR PP-OCRv5";
    private const string LanguageName = "中文/英文";

    private readonly Lazy<PaddleOcrAll> _engine = new(CreateEngine);
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private bool _isDisposed;

    public TextRecognitionMethod Method => TextRecognitionMethod.PaddleOcrV5;

    public TextRecognitionMethodOption GetOption()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return new TextRecognitionMethodOption(
                Method,
                MethodName,
                "使用 PaddleOCR PP-OCRv5 本地模型识别中英文截图文字",
                isAvailable: false,
                unavailableReason: "PaddleOCR 本地运行时目前仅配置了 x64 版本");
        }

        return new TextRecognitionMethodOption(
            Method,
            MethodName,
            "使用 PaddleOCR PP-OCRv5 本地模型识别中英文截图文字",
            isAvailable: true);
    }

    public async Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("PaddleOCR 本地运行时目前仅配置了 x64 版本。");
        }

        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (mat.Empty())
            {
                throw new InvalidOperationException("无法解码图像内容。");
            }

            var result = await Task.Run(() => _engine.Value.Run(mat), cancellationToken);
            return TextRecognitionResultFactory.Create(Method, MethodName, LanguageName, result.Text);
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    private static PaddleOcrAll CreateEngine()
    {
        return new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Blas())
        {
            AllowRotateDetection = false,
            Enable180Classification = false
        };
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
