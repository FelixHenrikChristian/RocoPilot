using System.Runtime.InteropServices.WindowsRuntime;

using RocoPilot.Models.TextRecognition;

using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RocoPilot.Services.TextRecognition.Backends;

public sealed class WindowsOcrTextRecognitionBackend : ITextRecognitionBackend
{
    private const string MethodName = "Windows OCR";

    public TextRecognitionMethod Method => TextRecognitionMethod.WindowsOcr;

    public TextRecognitionMethodOption GetOption()
    {
        var engine = CreateOcrEngine();
        if (engine is null)
        {
            return new TextRecognitionMethodOption(
                Method,
                MethodName,
                "使用 Windows 系统 OCR 能力识别图像文字",
                isAvailable: false,
                unavailableReason: "当前系统没有可用的 OCR 语言包");
        }

        return new TextRecognitionMethodOption(
            Method,
            MethodName,
            $"使用 Windows 系统 OCR 能力识别图像文字，当前语言：{engine.RecognizerLanguage.DisplayName}",
            isAvailable: true);
    }

    public async Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = CreateOcrEngine();
        if (engine is null)
        {
            throw new InvalidOperationException("当前系统没有可用的 OCR 语言包。");
        }

        using var stream = await CreateImageStreamAsync(imageBytes, cancellationToken);
        using var bitmap = await CreateSoftwareBitmapForOcrAsync(stream, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

        var lines = result.Lines
            .Select(line => line.Text)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        var wordCount = result.Lines.Sum(line => line.Words.Count);

        return new TextRecognitionResult(
            Method,
            MethodName,
            engine.RecognizerLanguage.DisplayName,
            lines,
            wordCount);
    }

    private static OcrEngine? CreateOcrEngine()
    {
        var userProfileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (userProfileEngine is not null)
        {
            return userProfileEngine;
        }

        var fallbackLanguage = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        return fallbackLanguage is null ? null : OcrEngine.TryCreateFromLanguage(fallbackLanguage);
    }

    private static async Task<InMemoryRandomAccessStream> CreateImageStreamAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);

        try
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
            writer.DetachStream();
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static async Task<SoftwareBitmap> CreateSoftwareBitmapForOcrAsync(
        IRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var transform = CreateOcrTransform(decoder);

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
    }

    private static BitmapTransform CreateOcrTransform(BitmapDecoder decoder)
    {
        var transform = new BitmapTransform();
        var longestSide = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        var maxDimension = OcrEngine.MaxImageDimension;
        if (longestSide <= maxDimension)
        {
            return transform;
        }

        var scale = (double)maxDimension / longestSide;
        transform.ScaledWidth = ScaleDimension(decoder.PixelWidth, scale);
        transform.ScaledHeight = ScaleDimension(decoder.PixelHeight, scale);
        return transform;
    }

    private static uint ScaleDimension(uint dimension, double scale)
    {
        var scaledDimension = (uint)Math.Round(dimension * scale);
        return scaledDimension == 0 ? 1 : scaledDimension;
    }
}
