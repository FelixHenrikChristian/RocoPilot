 using System.Runtime.InteropServices;

using OpenCvSharp;

using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;

using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class PaddleOcrV5TextRecognitionBackend : ITextRecognitionBackend, IFrameTextRecognitionBackend, IDisposable
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

    public async Task<TextRecognitionResult> RecognizeAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("PaddleOCR local execution is only configured for x64.");
        }

        ValidateFrameRegion(frame, region);

        await _recognitionLock.WaitAsync(cancellationToken);
        GCHandle pixelHandle = default;
        try
        {
            pixelHandle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            using var source = Mat.FromPixelData(
                frame.Height,
                frame.Width,
                MatType.CV_8UC4,
                pixelHandle.AddrOfPinnedObject());
            using var sourceRegion = new Mat(source, new Rect(region.X, region.Y, region.Width, region.Height));
            using var image = new Mat();
            Cv2.CvtColor(sourceRegion, image, ColorConversionCodes.BGRA2BGR);

            var text = await Task.Run(() => _engine.Value.Run(image).Text, cancellationToken);
            return TextRecognitionResultFactory.Create(Method, MethodName, LanguageName, text);
        }
        finally
        {
            if (pixelHandle.IsAllocated)
            {
                pixelHandle.Free();
            }

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

    private static void ValidateFrameRegion(CapturedFrame frame, RecognitionRegion region)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);

        var expectedPixelByteLength = checked(frame.Width * frame.Height * 4);
        if (frame.PixelByteLength < expectedPixelByteLength)
        {
            throw new InvalidDataException("Captured frame pixel data is incomplete.");
        }

        if (region.X < 0
            || region.Y < 0
            || region.Width <= 0
            || region.Height <= 0
            || region.X > frame.Width - region.Width
            || region.Y > frame.Height - region.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Recognition region must be inside the captured frame.");
        }
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
