using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Spirits;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.Spirits;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FlowerSeedScreenRecognizerTests
{
    [TestMethod]
    public async Task RealRareFlowerConfirmationUsesSharedHeaderAndFindsChallenge()
    {
        var images = new ImageMatchingService();
        var ocr = new NameOcr();
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = LoadFixture("flower-seed-confirmation.png");
        var options = new ImageMatchOptions { MinimumScore = .94 };
        var headerArea = new RecognitionRegion { X = 1024, Y = 288, Width = 573, Height = 288 };
        var oldHeader = await images.MatchAsync(frame, headerArea,
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "flower-seed-confirmation-fated-header.png"), options);
        var sharedHeader = await images.MatchAsync(frame, headerArea,
            "2048x1152/flower-seed/confirmation-header.png", options);
        var challenge = await images.MatchAsync(frame,
            new RecognitionRegion { X = 1024, Y = 852, Width = 451, Height = 230 },
            "2048x1152/flower-seed/confirmation-challenge.png", options);
        var cancel = await images.MatchAsync(frame,
            new RecognitionRegion { X = 614, Y = 852, Width = 451, Height = 230 },
            "2048x1152/flower-seed/confirmation-cancel.png", options);

        Assert.IsFalse(oldHeader.IsMatch, $"命定花种全标题得分：{oldHeader.Score:F4}");
        Assert.IsTrue(sharedHeader.IsMatch, $"花种公共标题得分：{sharedHeader.Score:F4}");
        Assert.IsTrue(challenge.IsMatch, $"挑战按钮得分：{challenge.Score:F4}");
        Assert.IsTrue(cancel.IsMatch, $"取消按钮得分：{cancel.Score:F4}");
        var screen = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);

        Assert.AreEqual(FlowerSeedScene.Confirmation, screen.Scene);
        Assert.IsNotNull(screen.Button);
        var clickX = screen.Button.X + screen.Button.Width / 2d;
        var clickY = screen.Button.Y + screen.Button.Height / 2d;
        Assert.IsTrue(clickX is >= 1068 and <= 1305 && clickY is >= 966 and <= 1032,
            $"点击位置未落在挑战按钮内：({clickX}, {clickY})");
        Assert.HasCount(0, ocr.Calls);
    }

    [TestMethod]
    public async Task SharedConfirmationHeaderStillMatchesOriginalFatedFlowerTitle()
    {
        using var header = LoadFixture("flower-seed-confirmation-fated-header.png");
        var pixels = new byte[2048 * 1152 * 4];
        Array.Fill(pixels, (byte)230);
        using var frame = new CapturedFrame(2048, 1152, pixels);
        for (var y = 0; y < header.Height; y++)
            Array.Copy(header.Pixels, y * header.Width * 4, frame.Pixels,
                ((y + 404) * frame.Width + 1230) * 4, header.Width * 4);

        var match = await new ImageMatchingService().MatchAsync(frame,
            new RecognitionRegion { X = 1024, Y = 288, Width = 573, Height = 288 },
            "2048x1152/flower-seed/confirmation-header.png", new ImageMatchOptions { MinimumScore = .94 });

        Assert.IsTrue(match.IsMatch, $"命定花种公共标题得分：{match.Score:F4}");
        Assert.AreEqual((1327, 404), (match.X, match.Y));
    }

    [TestMethod]
    public async Task RealRareFlowerMapUsesSharedHeaderAndFindsTeleportBeforeInteraction()
    {
        var images = new ImageMatchingService();
        var ocr = new NameOcr("卷毛鸭");
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = LoadFixture("flower-seed-map.png");
        var headerArea = new RecognitionRegion { X = 889, Y = 42, Width = 273, Height = 58 };
        var options = new ImageMatchOptions
        {
            MinimumScore = .94, TemplateScaleX = 768 / 1152d, TemplateScaleY = 768 / 1152d
        };
        var oldHeader = await images.MatchAsync(frame, headerArea,
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "flower-seed-map-fated-header.png"), options);
        var sharedHeader = await images.MatchAsync(frame, headerArea,
            "2048x1152/flower-seed/map-header.png", options);

        Assert.IsFalse(oldHeader.IsMatch, $"命定花种全标题得分：{oldHeader.Score:F4}");
        Assert.IsTrue(sharedHeader.IsMatch, $"花种公共标题得分：{sharedHeader.Score:F4}");
        var screen = await recognizer.ReadAsync(frame, 1, 30, 1366, 768, CancellationToken.None);

        Assert.AreEqual(FlowerSeedScene.Map, screen.Scene);
        Assert.IsNotNull(screen.Button);
        Assert.IsTrue(screen.Button.IsMatch, $"地图传送得分：{screen.Button.Score:F4}");
        var clickX = screen.Button.X + screen.Button.Width / 2d;
        var clickY = screen.Button.Y + screen.Button.Height / 2d;
        Assert.IsTrue(clickX is >= 903 and <= 1280 && clickY is >= 730 and <= 773,
            $"点击位置未落在地图传送按钮内：({clickX}, {clickY})");
        Assert.HasCount(1, ocr.Calls);
        Assert.AreEqual("地图花种名称", ocr.Calls[0].Region.Id);
    }

    [TestMethod]
    public async Task SharedMapHeaderStillMatchesOriginalFatedFlowerTitle()
    {
        using var header = LoadFixture("flower-seed-map-fated-header.png");
        var pixels = new byte[2048 * 1152 * 4];
        Array.Fill(pixels, (byte)230);
        using var frame = new CapturedFrame(2048, 1152, pixels);
        for (var y = 0; y < header.Height; y++)
            Array.Copy(header.Pixels, y * header.Width * 4, frame.Pixels,
                ((y + 38) * frame.Width + 1416) * 4, header.Width * 4);

        var match = await new ImageMatchingService().MatchAsync(frame,
            new RecognitionRegion { X = 1331, Y = 17, Width = 410, Height = 86 },
            "2048x1152/flower-seed/map-header.png", new ImageMatchOptions { MinimumScore = .94 });

        Assert.IsTrue(match.IsMatch, $"命定花种公共标题得分：{match.Score:F4}");
        Assert.AreEqual((1500, 38), (match.X, match.Y));
    }

    [TestMethod]
    public async Task EachVisibleNameUsesOneOcrReadAndCatalogCanonicalizesSpellingChanges()
    {
        var images = new ListImages(2048, 1152, 0, 0);
        var ocr = new NameOcr("友爰星飞", "小皮球", "友爱星飞", "小皮球");
        var catalog = new Catalog();
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, catalog, new Seasons(.70), NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = CreateFrame(2048, 1152, 0, 0);

        var first = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);
        Assert.AreEqual(FlowerSeedScene.FlowerList, first.Scene);
        CollectionAssert.AreEqual(new[] { "友爱星飞", "小皮球" }, first.Rows.Select(row => row.Name).ToArray());
        Assert.HasCount(2, ocr.Calls);

        var second = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);
        CollectionAssert.AreEqual(first.Rows.Select(row => row.Name).ToArray(), second.Rows.Select(row => row.Name).ToArray());
        Assert.HasCount(4, ocr.Calls);
        Assert.IsTrue(ocr.Calls.All(call => call.Method == TextRecognitionDefaults.Method));
        CollectionAssert.AreEqual(new[] { .70, .70, .70, .70 }, catalog.Thresholds.ToArray());
    }

    [TestMethod]
    public async Task CurrentCatalogThresholdAppliesAndRejectedRowsKeepTheirOriginalOcrName()
    {
        var images = new ListImages(2048, 1152, 0, 0);
        var ocr = new NameOcr("友爰星飞", "完全未知", "友爰星飞", "完全未知");
        var catalog = new Catalog();
        var seasons = new Seasons(.70);
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, catalog, seasons, NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = CreateFrame(2048, 1152, 0, 0);

        var accepted = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "友爱星飞", string.Empty }, accepted.Rows.Select(row => row.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "友爰星飞", "完全未知" }, accepted.Rows.Select(row => row.RawName).ToArray());

        seasons.Configuration.SpiritNameMatchThreshold = .90;
        var rejected = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);
        Assert.IsTrue(rejected.Rows.All(row => row.Name.Length == 0));
        CollectionAssert.AreEqual(new[] { "友爰星飞", "完全未知" }, rejected.Rows.Select(row => row.RawName).ToArray());
        CollectionAssert.AreEqual(new[] { .70, .70, .90, .90 }, catalog.Thresholds.ToArray());
        Assert.HasCount(4, ocr.Calls);
    }

    [TestMethod]
    public async Task OcrPreviewReportsTheActualCropAndRawAndMatchedNamesWithoutAnotherOcrRead()
    {
        var images = new ListImages(1280, 720, 12, 30);
        var ocr = new NameOcr("友爰星飞", "完全未知");
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = CreateFrame(1280, 720, 12, 30);
        List<(RecognitionRegion Region, string Text)> previews = [];

        var screen = await recognizer.ReadAsync(frame, 12, 30, 1280, 720, CancellationToken.None,
            (region, text) => previews.Add((region, text)));

        Assert.HasCount(2, ocr.Calls);
        Assert.HasCount(2, previews);
        for (var i = 0; i < 2; i++)
        {
            Assert.AreSame(ocr.Calls[i].Region, previews[i].Region);
            Assert.AreEqual($"花种名称 {i + 1}", previews[i].Region.Id);
        }
        Assert.AreEqual("OCR：友爰星飞 → 图鉴：友爱星飞", previews[0].Text);
        Assert.AreEqual("OCR：完全未知 → 图鉴：未匹配", previews[1].Text);
        CollectionAssert.AreEqual(new[] { "友爱星飞", string.Empty }, screen.Rows.Select(row => row.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "友爰星飞", "完全未知" }, screen.Rows.Select(row => row.RawName).ToArray());
    }

    [TestMethod]
    public async Task NameCropFollowsMatchedTeleportPositionAfterScrolling()
    {
        var images = new ListImages(2048, 1152, 0, 0);
        var ocr = new NameOcr("友爱星飞", "小皮球", "友爱星飞", "小皮球");
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = CreateFrame(2048, 1152, 0, 0);

        var before = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);
        for (var i = 0; i < images.Buttons.Length; i++)
            images.Buttons[i] = images.Buttons[i] with { Y = images.Buttons[i].Y - 40 };
        var after = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);

        Assert.HasCount(2, before.Rows);
        Assert.HasCount(2, after.Rows);
        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(ocr.Calls[i].Region.Y - 40, ocr.Calls[i + 2].Region.Y);
            Assert.AreEqual(ocr.Calls[i].Region.X, ocr.Calls[i + 2].Region.X);
            Assert.AreEqual(ocr.Calls[i].Region.Width, ocr.Calls[i + 2].Region.Width);
        }
    }

    [TestMethod]
    [DataRow(2048, 1152, 0, 0, 625, 231, 297, 50)]
    [DataRow(1280, 720, 12, 30, 402, 175, 186, 31)]
    [DataRow(2560, 1440, 18, 40, 799, 328, 371, 62)]
    public async Task NameCropUsesClientBoundsAndActualMatchedRowAtDifferentResolutions(
        int width, int height, int clientX, int clientY,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var images = new ListImages(width, height, clientX, clientY);
        var ocr = new NameOcr("友爱星飞", "小皮球");
        var recognizer = new FlowerSeedScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70), NullLogger<FlowerSeedScreenRecognizer>.Instance);
        using var frame = CreateFrame(width, height, clientX, clientY);

        var screen = await recognizer.ReadAsync(frame, clientX, clientY, width, height, CancellationToken.None);

        Assert.AreEqual(FlowerSeedScene.FlowerList, screen.Scene);
        Assert.HasCount(2, ocr.Calls);
        var crop = ocr.Calls[0].Region;
        Assert.AreEqual((expectedX, expectedY, expectedWidth, expectedHeight),
            (crop.X, crop.Y, crop.Width, crop.Height));
        Assert.AreSame(images.Buttons[0], screen.Rows[0].Button);
        Assert.AreEqual(height / 1152d, images.Scale);
        Assert.IsTrue(images.SearchRegion!.X >= clientX && images.SearchRegion.Y >= clientY);
        Assert.IsTrue(images.SearchRegion.X + images.SearchRegion.Width <= clientX + width);
        Assert.IsTrue(images.SearchRegion.Y + images.SearchRegion.Height <= clientY + height);
    }

    private static CapturedFrame LoadFixture(string fileName)
    {
        using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName), ImreadModes.Color);
        Assert.IsFalse(source.Empty(), $"未读取到测试样本：{fileName}");
        using var image = new Mat();
        Cv2.CvtColor(source, image, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[image.Width * image.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new(image.Width, image.Height, pixels);
    }

    private static CapturedFrame CreateFrame(int width, int height, int clientX, int clientY)
    {
        var frameWidth = width + clientX + 10;
        var frameHeight = height + clientY + 10;
        var pixels = new byte[frameWidth * frameHeight * 4];
        Array.Fill(pixels, (byte)245);
        return new(frameWidth, frameHeight, pixels);
    }

    private sealed class NameOcr(params string[] names) : ITextRecognitionService
    {
        private readonly Queue<string> _names = new(names);
        public List<(RecognitionRegion Region, TextRecognitionMethod Method)> Calls { get; } = [];
        public IReadOnlyList<TextRecognitionMethodOption> GetMethods() => [];
        public TextRecognitionMethodOption? GetDefaultMethod() => null;
        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TextRecognitionResult> RecognizeAsync(CapturedFrame frame, RecognitionRegion region,
            TextRecognitionMethod method, CancellationToken cancellationToken = default)
        {
            Calls.Add((region, method));
            return Task.FromResult(new TextRecognitionResult(method, "ONNX", null, [_names.Dequeue()], 1));
        }
    }

    private sealed class ListImages : IImageMatchingService
    {
        private readonly ImageMatchResult _tab;
        public ImageMatchResult[] Buttons { get; }
        public RecognitionRegion? SearchRegion { get; private set; }
        public double Scale { get; private set; }
        public ImageMatchAlgorithm DefaultAlgorithm => ImageMatchAlgorithm.OpenCvSqDiffNormalized;
        public string TemplateDirectory => string.Empty;
        public ListImages(int width, int height, int clientX, int clientY)
        {
            _tab = new(true, 1, clientX + (int)(width * .23), clientY + (int)(height * .04),
                (int)(width * .06), (int)(height * .03), "flower-tab");
            var buttonHeight = (int)Math.Round(height * .06);
            Buttons = Enumerable.Range(0, 2).Select(index => new ImageMatchResult(true, 1,
                clientX + (int)Math.Round(width * .80),
                clientY + (int)Math.Round(height * (.2625 + index * .2) - buttonHeight / 2d),
                (int)Math.Round(width * .10), buttonHeight, "list-teleport")).ToArray();
        }
        public IReadOnlyList<string> ListTemplatePaths() => [];
        public Task<ImageMatchResult> MatchAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            ImageMatchOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(templatePath.EndsWith("/flower-tab.png", StringComparison.Ordinal)
                ? _tab : ImageMatchResult.NoMatch(0, templatePath));
        public Task<ImageMatchCollectionResult> FindMatchesAsync(CapturedFrame frame, RecognitionRegion region,
            string templatePath, int maximumMatches, ImageMatchOptions? options = null,
            double maximumOverlapRatio = .5, CancellationToken cancellationToken = default)
        {
            SearchRegion = region;
            Scale = options!.TemplateScaleY;
            return Task.FromResult(new ImageMatchCollectionResult([Buttons[1], Buttons[0]], 1, templatePath));
        }
    }

    private sealed class Seasons(double threshold) : IEncounterSeasonConfigService
    {
        public EncounterSeasonConfig Configuration { get; } = new() { SpiritNameMatchThreshold = threshold };
        public EncounterSeasonConfig Load() => Configuration;
        public EncounterSeasonDefinition? GetCurrentSeason() => null;
    }

    private sealed class Catalog : ISpiritCatalogService
    {
        private readonly SpiritCatalogIndex _index = new(new SpiritCatalogDocument
        {
            Spirits = [new() { Name = "友爱星飞" }, new() { Name = "小皮球" }]
        });
        public List<double> Thresholds { get; } = [];
        public Task<string> MatchSpiritNameAsync(string recognizedText, double minimumSimilarity,
            CancellationToken cancellationToken = default)
        {
            Thresholds.Add(minimumSimilarity);
            return Task.FromResult(_index.Match(recognizedText, minimumSimilarity));
        }
        public Task<string> MatchSpiritNameAsync(string recognizedText,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<SpiritCatalogSourceOption> GetSources() => [];
        public Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpiritCatalogDocument> LoadAsync(string sourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpiritCatalogDocument> SyncAsync(IProgress<SpiritCatalogSyncProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpiritCatalogDocument> SyncAsync(string sourceId, IProgress<SpiritCatalogSyncProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveEvolutionRecordNameAsync(string spiritName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string? ResolveAvatarPath(string? avatarPath) => null;
    }
}
