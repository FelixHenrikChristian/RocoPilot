using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

using OpenCvSharp;

using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RocoPilot.Services.ImageMatching;

public sealed class ImageMatchingService : IImageMatchingService
{
    private readonly ConcurrentDictionary<TemplateCacheKey, Lazy<Task<ImageTemplate>>> _templateCache = new();

    // 当前模板匹配算法，用于诊断日志。
    public ImageMatchAlgorithm DefaultAlgorithm => ImageMatchAlgorithm.OpenCvSqDiffNormalized;

    public string TemplateDirectory
    {
        get;
    } = Path.Combine(AppContext.BaseDirectory, "Configuration", "RecognitionAssets", "ImageMatching");

    public async Task<ImageMatchResult> MatchAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        string templatePath,
        ImageMatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);

        if (string.IsNullOrWhiteSpace(templatePath))
        {
            throw new ArgumentException("Template path is required.", nameof(templatePath));
        }

        var resolvedTemplatePath = ResolveTemplatePath(templatePath);
        var normalizedOptions = NormalizeOptions(options);
        var (_, best) = await MatchTemplatesAsync(frame, region, resolvedTemplatePath,
            normalizedOptions, 1, .5, true, cancellationToken);
        return best;
    }

    public async Task<ImageMatchCollectionResult> FindMatchesAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        string templatePath,
        int maximumMatches,
        ImageMatchOptions? options = null,
        double maximumOverlapRatio = 0.5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);

        if (string.IsNullOrWhiteSpace(templatePath))
        {
            throw new ArgumentException("Template path is required.", nameof(templatePath));
        }

        if (maximumMatches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMatches), "Maximum matches must be greater than 0.");
        }

        if (double.IsNaN(maximumOverlapRatio) || maximumOverlapRatio < 0 || maximumOverlapRatio > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOverlapRatio),
                "Maximum overlap ratio must be between 0 and 1.");
        }

        var resolvedTemplatePath = ResolveTemplatePath(templatePath);
        var normalizedOptions = NormalizeOptions(options);
        var (matches, best) = await MatchTemplatesAsync(frame, region, resolvedTemplatePath,
            normalizedOptions, maximumMatches, maximumOverlapRatio, false, cancellationToken);
        return new ImageMatchCollectionResult(matches, best.Score, resolvedTemplatePath);
    }

    private string ResolveTemplatePath(string templatePath)
    {
        if (Path.IsPathRooted(templatePath))
        {
            return Path.GetFullPath(templatePath);
        }

        return Path.GetFullPath(Path.Combine(TemplateDirectory, templatePath));
    }

    private static ImageMatchOptions NormalizeOptions(ImageMatchOptions? options)
    {
        var normalized = new ImageMatchOptions
        {
            MinimumScore = options?.MinimumScore ?? 0.9,
            ScaleTolerance = options?.ScaleTolerance ?? .04,
            AlphaThreshold = options?.AlphaThreshold ?? 16,
            SearchStep = options?.SearchStep ?? 1,
            TemplateScaleX = NormalizeScale(options?.TemplateScaleX ?? 1),
            TemplateScaleY = NormalizeScale(options?.TemplateScaleY ?? 1)
        };
        if (double.IsNaN(normalized.MinimumScore) || normalized.MinimumScore < 0 || normalized.MinimumScore > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumScore must be between 0 and 1.");
        }

        if (normalized.SearchStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SearchStep must be greater than 0.");
        }

        if (!double.IsFinite(normalized.ScaleTolerance) || normalized.ScaleTolerance < 0 || normalized.ScaleTolerance >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ScaleTolerance must be between 0 (inclusive) and 1 (exclusive).");
        }

        return normalized;
    }

    // 单目标和列表共用匹配流程，截图只转换一次；不同尺度的候选统一去重。
    private async Task<(IReadOnlyList<ImageMatchResult> Matches, ImageMatchResult Best)> MatchTemplatesAsync(
        CapturedFrame frame, RecognitionRegion region, string templatePath, ImageMatchOptions options,
        int maximumMatches, double maximumOverlapRatio, bool singleMatch, CancellationToken cancellationToken)
    {
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var best = ImageMatchResult.NoMatch(0, templatePath);
        var searchArea = ClipRegion(region, frame);
        if (!region.Enabled || searchArea.Width <= 0 || searchArea.Height <= 0) return ([], best);

        var frameHandle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var frameBgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frameHandle.AddrOfPinnedObject());
            using var searchBgra = new Mat(frameBgra, new Rect(searchArea.X, searchArea.Y, searchArea.Width, searchArea.Height));
            using var searchBgr = new Mat();
            Cv2.CvtColor(searchBgra, searchBgr, ColorConversionCodes.BGRA2BGR);
            var candidates = new List<ImageMatchResult>();
            var sizes = new HashSet<(int, int)>();
            double[] factors = options.ScaleTolerance == 0 ? [1] :
                [1, 1 - options.ScaleTolerance / 2, 1 + options.ScaleTolerance / 2,
                    1 - options.ScaleTolerance, 1 + options.ScaleTolerance];
            foreach (var factor in factors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var template = await GetTemplateAsync(templatePath, options.AlphaThreshold,
                    NormalizeScale(options.TemplateScaleX * factor), NormalizeScale(options.TemplateScaleY * factor), cancellationToken);
                if (!sizes.Add((template.Width, template.Height))
                    || template.Width > searchArea.Width || template.Height > searchArea.Height) continue;

                var templateHandle = GCHandle.Alloc(template.BgrPixels, GCHandleType.Pinned);
                var maskHandle = GCHandle.Alloc(template.MaskPixels, GCHandleType.Pinned);
                try
                {
                    using var templateBgr = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC3, templateHandle.AddrOfPinnedObject());
                    using var mask = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC1, maskHandle.AddrOfPinnedObject());
                    using var result = new Mat();
                    Cv2.MatchTemplate(searchBgr, templateBgr, result, TemplateMatchModes.SqDiffNormed, mask);
                    cancellationToken.ThrowIfCancellationRequested();
                    Cv2.MinMaxLoc(result, out var minimum, out _, out var location, out _);
                    var score = SqDiffValueToScore(minimum);
                    var match = new ImageMatchResult(score >= options.MinimumScore, score,
                        searchArea.X + location.X, searchArea.Y + location.Y, template.Width, template.Height, templatePath);
                    if (best.Width == 0 || match.Score > best.Score) best = match;
                    if (singleMatch)
                    {
                        // 基准尺度已可靠命中时不额外搜索；未命中才比较周边尺度。
                        if (factor == 1 && match.IsMatch) return ([match], match);
                        continue;
                    }

                    var rows = result.Rows;
                    var columns = result.Cols;
                    for (var y = 0; y < rows; y += options.SearchStep)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (var x = 0; x < columns; x += options.SearchStep)
                        {
                            var candidateScore = SqDiffValueToScore(result.At<float>(y, x));
                            if (candidateScore < options.MinimumScore) continue;
                            candidates.Add(new ImageMatchResult(true, candidateScore, searchArea.X + x, searchArea.Y + y,
                                template.Width, template.Height, templatePath));
                        }
                    }
                }
                finally
                {
                    maskHandle.Free();
                    templateHandle.Free();
                }
            }

            if (singleMatch) return (best.IsMatch ? [best] : [], best);
            var matches = new List<ImageMatchResult>();
            foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score)
                         .ThenBy(candidate => candidate.Y).ThenBy(candidate => candidate.X))
            {
                if (matches.Any(match => CalculateOverlapRatio(candidate, match) > maximumOverlapRatio)) continue;
                matches.Add(candidate);
                if (matches.Count >= maximumMatches) break;
            }
            return (matches, best);
        }
        finally
        {
            frameHandle.Free();
        }
    }

    private static double SqDiffValueToScore(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(1 - value, 0, 1)
            : 0;
    }

    private static double NormalizeScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Template scale must be greater than 0.");
        }

        return Math.Round(scale, 4);
    }

    private async Task<ImageTemplate> LoadTemplateAsync(
        string templatePath,
        byte alphaThreshold,
        double scaleX,
        double scaleY,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Image matching template was not found.", templatePath);
        }

        var imageBytes = await File.ReadAllBytesAsync(templatePath, cancellationToken);
        using var stream = await CreateImageStreamAsync(imageBytes, cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var sourceWidth = checked((int)decoder.PixelWidth);
        var sourceHeight = checked((int)decoder.PixelHeight);
        var scaledWidth = Math.Max(1, (int)Math.Round(sourceWidth * scaleX));
        var scaledHeight = Math.Max(1, (int)Math.Round(sourceHeight * scaleY));
        var transform = new BitmapTransform();
        if (scaledWidth != sourceWidth || scaledHeight != sourceHeight)
        {
            transform.ScaledWidth = checked((uint)scaledWidth);
            transform.ScaledHeight = checked((uint)scaledHeight);
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());

        var template = ImageTemplate.Create(bitmap.PixelWidth, bitmap.PixelHeight, pixels, alphaThreshold);
        return template;
    }

    private async Task<ImageTemplate> GetTemplateAsync(
        string templatePath,
        byte alphaThreshold,
        double scaleX,
        double scaleY,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Image matching template was not found.", templatePath);
        }

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(templatePath);
        var cacheKey = new TemplateCacheKey(templatePath, alphaThreshold, scaleX, scaleY, lastWriteTimeUtc);
        var lazyTemplate = _templateCache.GetOrAdd(
            cacheKey,
            key => new Lazy<Task<ImageTemplate>>(
                () => LoadTemplateAsync(
                    key.TemplatePath,
                    key.AlphaThreshold,
                    key.ScaleX,
                    key.ScaleY,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var template = await lazyTemplate.Value.WaitAsync(cancellationToken);
        RemoveOutdatedTemplates(cacheKey);
        return template;
    }

    private void RemoveOutdatedTemplates(TemplateCacheKey currentKey)
    {
        foreach (var key in _templateCache.Keys)
        {
            if (!string.Equals(key.TemplatePath, currentKey.TemplatePath, StringComparison.OrdinalIgnoreCase)
                || key.AlphaThreshold != currentKey.AlphaThreshold
                || key.ScaleX != currentKey.ScaleX
                || key.ScaleY != currentKey.ScaleY
                || key.LastWriteTimeUtc == currentKey.LastWriteTimeUtc)
            {
                continue;
            }

            _ = _templateCache.TryRemove(key, out _);
        }
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

    private static double CalculateOverlapRatio(ImageMatchResult first, ImageMatchResult second)
    {
        var intersectionLeft = Math.Max(first.X, second.X);
        var intersectionTop = Math.Max(first.Y, second.Y);
        var intersectionRight = Math.Min(first.X + first.Width, second.X + second.Width);
        var intersectionBottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersectionWidth = Math.Max(0, intersectionRight - intersectionLeft);
        var intersectionHeight = Math.Max(0, intersectionBottom - intersectionTop);
        var intersectionArea = intersectionWidth * intersectionHeight;
        if (intersectionArea <= 0)
        {
            return 0;
        }

        var firstArea = first.Width * first.Height;
        var secondArea = second.Width * second.Height;
        var unionArea = firstArea + secondArea - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / (double)unionArea;
    }

    private static void ValidateFrame(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentException("Captured frame dimensions must be greater than 0.", nameof(frame));
        }

        var expectedLength = frame.Width * frame.Height * 4;
        if (frame.PixelByteLength < expectedLength)
        {
            throw new ArgumentException("Captured frame pixels must be BGRA32 data.", nameof(frame));
        }
    }

    private static SearchArea ClipRegion(RecognitionRegion region, CapturedFrame frame)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return new SearchArea(0, 0, 0, 0);
        }

        var left = Math.Clamp(region.X, 0, frame.Width);
        var top = Math.Clamp(region.Y, 0, frame.Height);
        var right = Math.Clamp(region.X + region.Width, 0, frame.Width);
        var bottom = Math.Clamp(region.Y + region.Height, 0, frame.Height);

        return right <= left || bottom <= top
            ? new SearchArea(0, 0, 0, 0)
            : new SearchArea(left, top, right - left, bottom - top);
    }

    private sealed class ImageTemplate
    {
        private ImageTemplate(
            int width,
            int height,
            byte[] bgrPixels,
            byte[] maskPixels)
        {
            Width = width;
            Height = height;
            BgrPixels = bgrPixels;
            MaskPixels = maskPixels;
        }

        public int Width
        {
            get;
        }

        public int Height
        {
            get;
        }

        public byte[] BgrPixels
        {
            get;
        }

        public byte[] MaskPixels
        {
            get;
        }

        public static ImageTemplate Create(int width, int height, byte[] pixels, byte alphaThreshold)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Template dimensions must be greater than 0.");
            }

            var expectedLength = width * height * 4;
            if (pixels.Length < expectedLength)
            {
                throw new ArgumentException("Template pixels must be BGRA32 data.", nameof(pixels));
            }

            var hasVisiblePixels = false;
            var bgrPixels = new byte[checked(width * height * 3)];
            var maskPixels = new byte[checked(width * height)];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = ((y * width) + x) * 4;
                    var pixelIndex = (y * width) + x;
                    var bgrOffset = pixelIndex * 3;
                    bgrPixels[bgrOffset] = pixels[offset];
                    bgrPixels[bgrOffset + 1] = pixels[offset + 1];
                    bgrPixels[bgrOffset + 2] = pixels[offset + 2];
                    var alpha = pixels[offset + 3];
                    if (alpha <= alphaThreshold)
                    {
                        continue;
                    }

                    maskPixels[pixelIndex] = byte.MaxValue;
                    hasVisiblePixels = true;
                }
            }

            if (!hasVisiblePixels)
            {
                throw new InvalidOperationException("Image matching template has no visible pixels.");
            }

            return new ImageTemplate(
                width,
                height,
                bgrPixels,
                maskPixels);
        }
    }

    private readonly record struct SearchArea(int X, int Y, int Width, int Height);

    private readonly record struct TemplateCacheKey(
        string TemplatePath,
        byte AlphaThreshold,
        double ScaleX,
        double ScaleY,
        DateTime LastWriteTimeUtc);
}
