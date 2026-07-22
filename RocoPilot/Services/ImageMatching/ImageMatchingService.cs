using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.Extensions.Logging;

using OpenCvSharp;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RocoPilot.Services.ImageMatching;

public sealed class ImageMatchingService : IImageMatchingService
{
    private static readonly string[] TemplateExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff"
    ];

    private readonly ConcurrentDictionary<TemplateCacheKey, Lazy<Task<ImageTemplate>>> _templateCache = new();
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<ImageMatchingService> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private int _defaultAlgorithm = (int)ImageMatchAlgorithm.OpenCvSqDiffNormalized;
    private bool _isInitialized;

    public ImageMatchingService(
        ILocalSettingsService localSettingsService,
        ILogger<ImageMatchingService> logger)
    {
        _localSettingsService = localSettingsService;
        _logger = logger;
        TemplateDirectory = Path.Combine(AppContext.BaseDirectory, "Configuration", "RecognitionAssets", "ImageMatching");
    }

    public ImageMatchAlgorithm DefaultAlgorithm =>
        (ImageMatchAlgorithm)Volatile.Read(ref _defaultAlgorithm);

    public string TemplateDirectory
    {
        get;
    }

    public IReadOnlyList<string> ListTemplatePaths()
    {
        if (!Directory.Exists(TemplateDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(TemplateDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => TemplateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isInitialized))
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            var savedAlgorithm =
                await _localSettingsService.ReadSettingAsync<ImageMatchAlgorithm?>(SettingsKeys.ImageMatchAlgorithm);
            var algorithm = IsConcreteAlgorithm(savedAlgorithm)
                ? savedAlgorithm!.Value
                : ImageMatchAlgorithm.OpenCvSqDiffNormalized;
            Volatile.Write(ref _defaultAlgorithm, (int)algorithm);
            Volatile.Write(ref _isInitialized, true);
            _logger.LogDebug("模板匹配算法已加载：Algorithm={Algorithm}", algorithm);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _defaultAlgorithm, (int)ImageMatchAlgorithm.OpenCvSqDiffNormalized);
            Volatile.Write(ref _isInitialized, true);
            _logger.LogWarning(
                ex,
                "读取模板匹配算法失败，已使用默认算法：Algorithm={Algorithm}",
                ImageMatchAlgorithm.OpenCvSqDiffNormalized);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task SetDefaultAlgorithmAsync(
        ImageMatchAlgorithm algorithm,
        CancellationToken cancellationToken = default)
    {
        if (!IsConcreteAlgorithm(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), "A concrete image matching algorithm is required.");
        }

        await InitializeAsync(cancellationToken);
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (DefaultAlgorithm == algorithm)
            {
                return;
            }

            await _localSettingsService.SaveSettingAsync(SettingsKeys.ImageMatchAlgorithm, algorithm);
            Volatile.Write(ref _defaultAlgorithm, (int)algorithm);
            _logger.LogInformation("模板匹配算法已切换：Algorithm={Algorithm}", algorithm);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<ImageMatchResult> MatchAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        string templatePath,
        ImageMatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(region);
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(templatePath))
        {
            throw new ArgumentException("Template path is required.", nameof(templatePath));
        }

        var resolvedTemplatePath = ResolveTemplatePath(templatePath);
        var normalizedOptions = NormalizeOptions(options);
        var template = await GetTemplateAsync(
            resolvedTemplatePath,
            normalizedOptions.AlphaThreshold,
            normalizedOptions.TemplateScaleX,
            normalizedOptions.TemplateScaleY,
            cancellationToken);

        return normalizedOptions.Algorithm switch
        {
            ImageMatchAlgorithm.OpenCvSqDiffNormalized => MatchTemplateWithOpenCvSqDiffNormalized(
                frame,
                region,
                template,
                resolvedTemplatePath,
                normalizedOptions,
                cancellationToken),
            _ => MatchTemplate(frame, region, template, resolvedTemplatePath, normalizedOptions, cancellationToken)
        };
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
        await InitializeAsync(cancellationToken);

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
        var template = await GetTemplateAsync(
            resolvedTemplatePath,
            normalizedOptions.AlphaThreshold,
            normalizedOptions.TemplateScaleX,
            normalizedOptions.TemplateScaleY,
            cancellationToken);

        return normalizedOptions.Algorithm switch
        {
            ImageMatchAlgorithm.OpenCvSqDiffNormalized => FindTemplateMatchesWithOpenCvSqDiffNormalized(
                frame,
                region,
                template,
                resolvedTemplatePath,
                maximumMatches,
                maximumOverlapRatio,
                normalizedOptions,
                cancellationToken),
            _ => FindTemplateMatches(
                frame,
                region,
                template,
                resolvedTemplatePath,
                maximumMatches,
                maximumOverlapRatio,
                normalizedOptions,
                cancellationToken)
        };
    }

    private string ResolveTemplatePath(string templatePath)
    {
        if (Path.IsPathRooted(templatePath))
        {
            return Path.GetFullPath(templatePath);
        }

        return Path.GetFullPath(Path.Combine(TemplateDirectory, templatePath));
    }

    private ImageMatchOptions NormalizeOptions(ImageMatchOptions? options)
    {
        var requestedAlgorithm = options?.Algorithm ?? ImageMatchAlgorithm.UseGlobalDefault;
        var normalized = new ImageMatchOptions
        {
            Algorithm = requestedAlgorithm == ImageMatchAlgorithm.UseGlobalDefault
                ? DefaultAlgorithm
                : requestedAlgorithm,
            MinimumScore = options?.MinimumScore ?? 0.9,
            AlphaThreshold = options?.AlphaThreshold ?? 16,
            SearchStep = options?.SearchStep ?? 1,
            TemplateScaleX = NormalizeScale(options?.TemplateScaleX ?? 1),
            TemplateScaleY = NormalizeScale(options?.TemplateScaleY ?? 1)
        };
        if (!IsConcreteAlgorithm(normalized.Algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A supported image matching algorithm is required.");
        }

        if (double.IsNaN(normalized.MinimumScore) || normalized.MinimumScore < 0 || normalized.MinimumScore > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumScore must be between 0 and 1.");
        }

        if (normalized.SearchStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SearchStep must be greater than 0.");
        }

        return normalized;
    }

    private static bool IsConcreteAlgorithm(ImageMatchAlgorithm? algorithm)
    {
        return algorithm is ImageMatchAlgorithm.WeightedRgbError
            or ImageMatchAlgorithm.OpenCvSqDiffNormalized;
    }

    private static ImageMatchResult MatchTemplateWithOpenCvSqDiffNormalized(
        CapturedFrame frame,
        RecognitionRegion region,
        ImageTemplate template,
        string templatePath,
        ImageMatchOptions options,
        CancellationToken cancellationToken)
    {
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (!region.Enabled)
        {
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        var searchArea = ClipRegion(region, frame);
        if (searchArea.Width < template.Width || searchArea.Height < template.Height)
        {
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        GCHandle frameHandle = default;
        GCHandle templateHandle = default;
        GCHandle maskHandle = default;
        try
        {
            frameHandle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            templateHandle = GCHandle.Alloc(template.BgrPixels, GCHandleType.Pinned);
            maskHandle = GCHandle.Alloc(template.MaskPixels, GCHandleType.Pinned);

            using var frameBgra = Mat.FromPixelData(
                frame.Height,
                frame.Width,
                MatType.CV_8UC4,
                frameHandle.AddrOfPinnedObject());
            using var searchBgra = new Mat(
                frameBgra,
                new Rect(searchArea.X, searchArea.Y, searchArea.Width, searchArea.Height));
            using var searchBgr = new Mat();
            Cv2.CvtColor(searchBgra, searchBgr, ColorConversionCodes.BGRA2BGR);
            using var templateBgr = Mat.FromPixelData(
                template.Height,
                template.Width,
                MatType.CV_8UC3,
                templateHandle.AddrOfPinnedObject());
            using var templateMask = Mat.FromPixelData(
                template.Height,
                template.Width,
                MatType.CV_8UC1,
                maskHandle.AddrOfPinnedObject());
            using var result = new Mat();
            Cv2.MatchTemplate(
                searchBgr,
                templateBgr,
                result,
                TemplateMatchModes.SqDiffNormed,
                templateMask);
            cancellationToken.ThrowIfCancellationRequested();
            Cv2.MinMaxLoc(
                result,
                out var minimumValue,
                out _,
                out var minimumLocation,
                out _);
            var score = SqDiffValueToScore(minimumValue);
            return new ImageMatchResult(
                score >= options.MinimumScore,
                score,
                searchArea.X + minimumLocation.X,
                searchArea.Y + minimumLocation.Y,
                template.Width,
                template.Height,
                templatePath);
        }
        finally
        {
            if (maskHandle.IsAllocated)
            {
                maskHandle.Free();
            }

            if (templateHandle.IsAllocated)
            {
                templateHandle.Free();
            }

            if (frameHandle.IsAllocated)
            {
                frameHandle.Free();
            }
        }
    }

    private static ImageMatchCollectionResult FindTemplateMatchesWithOpenCvSqDiffNormalized(
        CapturedFrame frame,
        RecognitionRegion region,
        ImageTemplate template,
        string templatePath,
        int maximumMatches,
        double maximumOverlapRatio,
        ImageMatchOptions options,
        CancellationToken cancellationToken)
    {
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (!region.Enabled)
        {
            return ImageMatchCollectionResult.NoMatch(0, templatePath);
        }

        var searchArea = ClipRegion(region, frame);
        if (searchArea.Width < template.Width || searchArea.Height < template.Height)
        {
            return ImageMatchCollectionResult.NoMatch(0, templatePath);
        }

        GCHandle frameHandle = default;
        GCHandle templateHandle = default;
        GCHandle maskHandle = default;
        try
        {
            frameHandle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            templateHandle = GCHandle.Alloc(template.BgrPixels, GCHandleType.Pinned);
            maskHandle = GCHandle.Alloc(template.MaskPixels, GCHandleType.Pinned);

            using var frameBgra = Mat.FromPixelData(
                frame.Height,
                frame.Width,
                MatType.CV_8UC4,
                frameHandle.AddrOfPinnedObject());
            using var searchBgra = new Mat(
                frameBgra,
                new Rect(searchArea.X, searchArea.Y, searchArea.Width, searchArea.Height));
            using var searchBgr = new Mat();
            Cv2.CvtColor(searchBgra, searchBgr, ColorConversionCodes.BGRA2BGR);
            using var templateBgr = Mat.FromPixelData(
                template.Height,
                template.Width,
                MatType.CV_8UC3,
                templateHandle.AddrOfPinnedObject());
            using var templateMask = Mat.FromPixelData(
                template.Height,
                template.Width,
                MatType.CV_8UC1,
                maskHandle.AddrOfPinnedObject());
            using var result = new Mat();
            Cv2.MatchTemplate(
                searchBgr,
                templateBgr,
                result,
                TemplateMatchModes.SqDiffNormed,
                templateMask);

            var candidates = new List<ImageMatchResult>();
            var bestScore = 0d;
            var resultRows = result.Rows;
            var resultColumns = result.Cols;
            for (var y = 0; y < resultRows; y += options.SearchStep)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (var x = 0; x < resultColumns; x += options.SearchStep)
                {
                    var score = SqDiffValueToScore(result.At<float>(y, x));
                    bestScore = Math.Max(bestScore, score);
                    if (score < options.MinimumScore)
                    {
                        continue;
                    }

                    candidates.Add(new ImageMatchResult(
                        true,
                        score,
                        searchArea.X + x,
                        searchArea.Y + y,
                        template.Width,
                        template.Height,
                        templatePath));
                }
            }

            var matches = new List<ImageMatchResult>(Math.Min(maximumMatches, candidates.Count));
            foreach (var candidate in candidates
                         .OrderByDescending(candidate => candidate.Score)
                         .ThenBy(candidate => candidate.Y)
                         .ThenBy(candidate => candidate.X))
            {
                if (matches.Any(match => CalculateOverlapRatio(candidate, match) > maximumOverlapRatio))
                {
                    continue;
                }

                matches.Add(candidate);
                if (matches.Count >= maximumMatches)
                {
                    break;
                }
            }

            return new ImageMatchCollectionResult(matches, bestScore, templatePath);
        }
        finally
        {
            if (maskHandle.IsAllocated)
            {
                maskHandle.Free();
            }

            if (templateHandle.IsAllocated)
            {
                templateHandle.Free();
            }

            if (frameHandle.IsAllocated)
            {
                frameHandle.Free();
            }
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

    private static ImageMatchResult MatchTemplate(
        CapturedFrame frame,
        RecognitionRegion region,
        ImageTemplate template,
        string templatePath,
        ImageMatchOptions options,
        CancellationToken cancellationToken)
    {
        ValidateFrame(frame);

        if (!region.Enabled)
        {
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        var searchArea = ClipRegion(region, frame);
        if (searchArea.Width < template.Width || searchArea.Height < template.Height)
        {
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        var bestScore = double.NegativeInfinity;
        var bestX = searchArea.X;
        var bestY = searchArea.Y;
        var maxX = searchArea.Right - template.Width;
        var maxY = searchArea.Bottom - template.Height;

        for (var y = searchArea.Y; y <= maxY; y += options.SearchStep)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var x = searchArea.X; x <= maxX; x += options.SearchStep)
            {
                double? competitiveScore = bestScore >= options.MinimumScore
                    ? Math.Max(bestScore, options.MinimumScore)
                    : null;
                var score = ScoreAt(frame, template, x, y, competitiveScore);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestX = x;
                bestY = y;
            }
        }

        if (double.IsNegativeInfinity(bestScore))
        {
            return ImageMatchResult.NoMatch(0, templatePath);
        }

        return new ImageMatchResult(
            bestScore >= options.MinimumScore,
            bestScore,
            bestX,
            bestY,
            template.Width,
            template.Height,
            templatePath);
    }

    private static ImageMatchCollectionResult FindTemplateMatches(
        CapturedFrame frame,
        RecognitionRegion region,
        ImageTemplate template,
        string templatePath,
        int maximumMatches,
        double maximumOverlapRatio,
        ImageMatchOptions options,
        CancellationToken cancellationToken)
    {
        ValidateFrame(frame);

        if (!region.Enabled)
        {
            return ImageMatchCollectionResult.NoMatch(0, templatePath);
        }

        var searchArea = ClipRegion(region, frame);
        if (searchArea.Width < template.Width || searchArea.Height < template.Height)
        {
            return ImageMatchCollectionResult.NoMatch(0, templatePath);
        }

        var candidates = new List<ImageMatchResult>();
        var bestScore = double.NegativeInfinity;
        var maxX = searchArea.Right - template.Width;
        var maxY = searchArea.Bottom - template.Height;

        for (var y = searchArea.Y; y <= maxY; y += options.SearchStep)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var x = searchArea.X; x <= maxX; x += options.SearchStep)
            {
                double? competitiveScore = candidates.Count > 0
                    ? options.MinimumScore
                    : null;
                var score = ScoreAt(frame, template, x, y, competitiveScore);
                bestScore = Math.Max(bestScore, score);
                if (score < options.MinimumScore)
                {
                    continue;
                }

                candidates.Add(new ImageMatchResult(
                    true,
                    score,
                    x,
                    y,
                    template.Width,
                    template.Height,
                    templatePath));
            }
        }

        if (double.IsNegativeInfinity(bestScore))
        {
            bestScore = 0;
        }

        var matches = new List<ImageMatchResult>(Math.Min(maximumMatches, candidates.Count));
        foreach (var candidate in candidates
                     .OrderByDescending(candidate => candidate.Score)
                     .ThenBy(candidate => candidate.Y)
                     .ThenBy(candidate => candidate.X))
        {
            if (matches.Any(match => CalculateOverlapRatio(candidate, match) > maximumOverlapRatio))
            {
                continue;
            }

            matches.Add(candidate);
            if (matches.Count >= maximumMatches)
            {
                break;
            }
        }

        return new ImageMatchCollectionResult(matches, bestScore, templatePath);
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

    private static double ScoreAt(
        CapturedFrame frame,
        ImageTemplate template,
        int originX,
        int originY,
        double? competitiveScore)
    {
        var weightedError = 0d;
        var maximumWeightedError = template.TotalWeight * 765d;
        var earlyExitError = competitiveScore.HasValue
            ? (1d - competitiveScore.Value) * maximumWeightedError
            : double.PositiveInfinity;

        foreach (var pixel in template.ActivePixels)
        {
            var frameOffset = (((originY + pixel.Y) * frame.Width) + originX + pixel.X) * 4;
            var error =
                Math.Abs(frame.Pixels[frameOffset] - pixel.Blue)
                + Math.Abs(frame.Pixels[frameOffset + 1] - pixel.Green)
                + Math.Abs(frame.Pixels[frameOffset + 2] - pixel.Red);

            weightedError += error * pixel.Weight;
            if (weightedError > earlyExitError)
            {
                return 0;
            }
        }

        return Math.Clamp(1d - (weightedError / maximumWeightedError), 0d, 1d);
    }

    private sealed class ImageTemplate
    {
        private ImageTemplate(
            int width,
            int height,
            IReadOnlyList<TemplatePixel> activePixels,
            double totalWeight,
            byte[] bgrPixels,
            byte[] maskPixels)
        {
            Width = width;
            Height = height;
            ActivePixels = activePixels;
            TotalWeight = totalWeight;
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

        public IReadOnlyList<TemplatePixel> ActivePixels
        {
            get;
        }

        public double TotalWeight
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

            var activePixels = new List<TemplatePixel>();
            var totalWeight = 0d;
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

                    var weight = alpha / 255d;
                    activePixels.Add(new TemplatePixel(
                        x,
                        y,
                        pixels[offset],
                        pixels[offset + 1],
                        pixels[offset + 2],
                        weight));
                    totalWeight += weight;
                }
            }

            if (activePixels.Count == 0)
            {
                throw new InvalidOperationException("Image matching template has no visible pixels.");
            }

            return new ImageTemplate(
                width,
                height,
                activePixels,
                totalWeight,
                bgrPixels,
                maskPixels);
        }
    }

    private readonly record struct TemplatePixel(
        int X,
        int Y,
        byte Blue,
        byte Green,
        byte Red,
        double Weight);

    private readonly record struct SearchArea(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }

    private readonly record struct TemplateCacheKey(
        string TemplatePath,
        byte AlphaThreshold,
        double ScaleX,
        double ScaleY,
        DateTime LastWriteTimeUtc);
}
