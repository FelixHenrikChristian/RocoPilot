using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.Extensions.Logging;

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

    private readonly ILogger<ImageMatchingService> _logger;
    private readonly ConcurrentDictionary<TemplateCacheKey, Lazy<Task<ImageTemplate>>> _templateCache = new();

    public ImageMatchingService(ILogger<ImageMatchingService> logger)
    {
        _logger = logger;
        TemplateDirectory = Path.Combine(AppContext.BaseDirectory, "Configuration", "RecognitionAssets", "ImageMatching");
    }

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
        var template = await GetTemplateAsync(
            resolvedTemplatePath,
            normalizedOptions.AlphaThreshold,
            cancellationToken);

        return MatchTemplate(frame, region, template, resolvedTemplatePath, normalizedOptions, cancellationToken);
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
        var normalized = options ?? new ImageMatchOptions();
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

    private async Task<ImageTemplate> LoadTemplateAsync(
        string templatePath,
        byte alphaThreshold,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Image matching template was not found.", templatePath);
        }

        var imageBytes = await File.ReadAllBytesAsync(templatePath, cancellationToken);
        using var stream = await CreateImageStreamAsync(imageBytes, cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());

        var template = ImageTemplate.Create(bitmap.PixelWidth, bitmap.PixelHeight, pixels, alphaThreshold);
        _logger.LogDebug(
            "Loaded image matching template {TemplatePath}: {Width}x{Height}, active pixels: {ActivePixelCount}",
            templatePath,
            template.Width,
            template.Height,
            template.ActivePixels.Count);

        return template;
    }

    private async Task<ImageTemplate> GetTemplateAsync(
        string templatePath,
        byte alphaThreshold,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Image matching template was not found.", templatePath);
        }

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(templatePath);
        var cacheKey = new TemplateCacheKey(templatePath, alphaThreshold, lastWriteTimeUtc);
        var lazyTemplate = _templateCache.GetOrAdd(
            cacheKey,
            key => new Lazy<Task<ImageTemplate>>(
                () => LoadTemplateAsync(key.TemplatePath, key.AlphaThreshold, CancellationToken.None),
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

    private static void ValidateFrame(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentException("Captured frame dimensions must be greater than 0.", nameof(frame));
        }

        var expectedLength = frame.Width * frame.Height * 4;
        if (frame.Pixels.Length < expectedLength)
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
        private ImageTemplate(int width, int height, IReadOnlyList<TemplatePixel> activePixels, double totalWeight)
        {
            Width = width;
            Height = height;
            ActivePixels = activePixels;
            TotalWeight = totalWeight;
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

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = ((y * width) + x) * 4;
                    var alpha = pixels[offset + 3];
                    if (alpha <= alphaThreshold)
                    {
                        continue;
                    }

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

            return new ImageTemplate(width, height, activePixels, totalWeight);
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
        DateTime LastWriteTimeUtc);
}
