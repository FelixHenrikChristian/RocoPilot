using System.Runtime.InteropServices;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using OpenCvSharp;
using OpenCvSharp.Dnn;

using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class OnnxOcrV5SingleLineTextRecognitionBackend : ISingleLineTextRecognitionBackend, IDisposable
{
    private const string MethodName = "ONNX Runtime PP-OCRv5";
    private const string LanguageName = "Chinese/English";
    private const int ModelHeight = 48;
    private const int MaximumWidth = 320;

    private readonly Lazy<OnnxOcrV5Recognizer?> _recognizer = new(CreateRecognizer);
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private bool _isDisposed;

    public TextRecognitionMethod Method => TextRecognitionMethod.PaddleOcrV5;

    public bool IsAvailable => !_isDisposed && _recognizer.Value is not null;

    public Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return Task.Run(
            async () =>
            {
                try
                {
                    using var frame = new CapturedFrame(1, 1, new byte[4]);
                    var region = new RecognitionRegion { Id = "onnx-ocr-prewarm", Width = 1, Height = 1 };
                    _ = await RecognizeAsync(frame, region, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Keep PaddleOCR available if the optional ONNX warmup cannot complete.
                }
            },
            cancellationToken);
    }

    public async Task<TextRecognitionResult> RecognizeAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrameRegion(frame, region);

        var recognizer = _recognizer.Value
            ?? throw new InvalidOperationException("The ONNX OCR recognizer is unavailable.");

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

            var text = await Task.Run(() => recognizer.Recognize(image), cancellationToken);
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_recognizer.IsValueCreated)
        {
            _recognizer.Value?.Dispose();
        }

        _recognitionLock.Dispose();
        _isDisposed = true;
    }

    private static OnnxOcrV5Recognizer? CreateRecognizer()
    {
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "OCR", "Onnx", "PaddleOcrV5");
        var modelPath = Path.Combine(modelDirectory, "inference.onnx");
        var configurationPath = Path.Combine(modelDirectory, "inference.yml");
        if (!File.Exists(modelPath) || !File.Exists(configurationPath))
        {
            return null;
        }

        try
        {
            return new OnnxOcrV5Recognizer(modelPath, configurationPath);
        }
        catch (Exception)
        {
            return null;
        }
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

    private sealed class OnnxOcrV5Recognizer : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly IReadOnlyList<string> _labels;

        public OnnxOcrV5Recognizer(string modelPath, string configurationPath)
        {
            _labels = LoadLabels(configurationPath);
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            _session = new InferenceSession(modelPath, options);
        }

        public string Recognize(Mat source)
        {
            if (source.Empty())
            {
                throw new ArgumentException("OCR input image is empty.", nameof(source));
            }

            var resizedWidth = Math.Clamp(
                (int)Math.Ceiling(source.Width * ModelHeight / (double)source.Height),
                1,
                MaximumWidth);
            using var resized = new Mat();
            Cv2.Resize(source, resized, new Size(resizedWidth, ModelHeight), 0, 0, InterpolationFlags.Linear);
            using var blob = CvDnn.BlobFromImage(
                resized,
                2f / 255f,
                default,
                new Scalar(127.5, 127.5, 127.5),
                swapRB: false,
                crop: false);

            var input = new DenseTensor<float>(
                blob.AsSpan<float>().ToArray(),
                [1, 3, ModelHeight, resizedWidth]);
            using var results = _session.Run(
            [
                NamedOnnxValue.CreateFromTensor(_session.InputNames[0], input)
            ]);
            var output = results[0].AsTensor<float>();
            var dimensions = output.Dimensions;
            if (dimensions.Length != 3 || dimensions[0] != 1)
            {
                throw new InvalidDataException("Unexpected ONNX OCR output dimensions.");
            }

            return OnnxOcrV5TextDecoder.Decode(
                _labels,
                output.ToArray(),
                dimensions[1],
                dimensions[2]);
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        private static IReadOnlyList<string> LoadLabels(string configurationPath)
        {
            var labels = new List<string>();
            var readingCharacterDictionary = false;
            foreach (var line in File.ReadLines(configurationPath))
            {
                if (line.Trim() == "character_dict:")
                {
                    readingCharacterDictionary = true;
                    continue;
                }

                if (!readingCharacterDictionary)
                {
                    continue;
                }

                if (!line.StartsWith("  - ", StringComparison.Ordinal))
                {
                    break;
                }

                labels.Add(line[4..]);
            }

            if (labels.Count == 0)
            {
                throw new InvalidDataException("The ONNX OCR label dictionary is empty.");
            }

            return labels;
        }
    }
}
