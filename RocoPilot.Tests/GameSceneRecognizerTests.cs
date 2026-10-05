using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.ImageMatching;

namespace RocoPilot.Tests;

[TestClass]
public sealed class GameSceneRecognizerTests
{
    [TestMethod]
    [DataRow("battle-chat-handbook.png", false)]
    [DataRow("battle-chat-visible.png", true)]
    public async Task RealChatImagesDistinguishHandbookFromBattle(
        string sample, bool expected)
    {
        var matching = new ImageMatchingService();
        var debug = new RuntimeDebugLogger(NullLogger<RuntimeDebugLogger>.Instance);
        var recognizer = new BattleScreenRecognizer(new RuntimeFrameRecognizer(matching, null!, new OverlayStub(), debug));
        var samplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", sample);
        using var crop = Cv2.ImRead(samplePath, ImreadModes.Unchanged);
        Assert.IsFalse(crop.Empty(), $"未读取到测试样本：{samplePath}");
        Assert.AreEqual(4, crop.Channels());
        var rows = crop.Rows;
        var columns = crop.Cols;
        using var frame = CapturedFrame.RentBgra32(2048, 1152);
        var pixels = new byte[rows * columns * 4];
        System.Runtime.InteropServices.Marshal.Copy(crop.Data, pixels, 0, pixels.Length);
        for (var y = 0; y < rows; y++)
        {
            Array.Copy(pixels, y * columns * 4, frame.Pixels, ((y + 728) * frame.Width + 1918) * 4, columns * 4);
        }
        var state = new RuntimeTaskState(
            new CaptureTargetWindow { Hwnd = 1, ClientWidth = 2048, ClientHeight = 1152 },
            new RecognitionRegionConfig
            {
                ResolutionWidth = 2048, ResolutionHeight = 1152,
                Regions = [new() { Id = "battle-button-chat", X = 1918, Y = 728, Width = 105, Height = 105, Enabled = true }]
            },
            new RuntimeTaskStartOptions(), DateTimeOffset.Now);

        Assert.AreEqual(expected, await recognizer.IsBattleChatVisibleAsync(state, frame, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorldRecognitionDoesNotDependOnAutoBattleAndTakesPriority(bool autoBattleEnabled)
    {
        using var fixture = new SceneFixture(autoBattleEnabled);
        fixture.Matching.MagicPoints = 4;
        fixture.Matching.MatchedTemplate = "battle-chat.png";

        var result = await fixture.RecognizeAsync();

        Assert.AreEqual(GameScene.World, result.Scene);
        Assert.AreEqual(4, result.MagicPointCount);
        Assert.IsNull(result.BattleScreen);
        Assert.HasCount(0, fixture.Matching.SingleMatches);
        CollectionAssert.AreEqual(new[] { ("magic-point", 0.98) }, fixture.Overlay.Results);
    }

    [TestMethod]
    [DataRow("battle-button-skill.png", "battle-button-skill", BattleScreen.SkillSelection)]
    [DataRow("battle-button-change.png", "battle-button-change", BattleScreen.PetSwitching)]
    [DataRow("battle-button-change.png", "battle-button-skill", BattleScreen.PetSwitching)]
    [DataRow("battle-chat.png", "battle-button-chat", BattleScreen.Chat)]
    public async Task BattleRequiresPositiveImageEvidenceEvenWhenAutoBattleIsDisabled(
        string template, string region, BattleScreen expected)
    {
        using var fixture = new SceneFixture(autoBattleEnabled: false);
        fixture.Matching.MatchedTemplate = template;
        fixture.Matching.MatchedRegion = region;

        var result = await fixture.RecognizeAsync();

        Assert.AreEqual(GameScene.Battle, result.Scene);
        Assert.AreEqual(expected, result.BattleScreen);
        Assert.AreEqual(0, result.MagicPointCount);
    }

    [TestMethod]
    public async Task LosingAllImageEvidenceReturnsUnknownImmediatelyAfterBattle()
    {
        using var fixture = new SceneFixture();
        fixture.Matching.MatchedTemplate = "battle-chat.png";
        Assert.AreEqual(GameScene.Battle, (await fixture.RecognizeAsync()).Scene);

        fixture.Matching.MatchedTemplate = null;
        var result = await fixture.RecognizeAsync();

        Assert.AreEqual(GameScene.Unknown, result.Scene);
        Assert.IsNull(result.BattleScreen);
        Assert.AreEqual(0, result.MagicPointCount);
    }

    [TestMethod]
    public async Task MissingTemplatesDoNotImplyBattle()
    {
        using var fixture = new SceneFixture();
        foreach (var path in Directory.GetFiles(fixture.Matching.TemplateDirectory, "*.png", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        var result = await fixture.RecognizeAsync();

        Assert.AreEqual(GameScene.Unknown, result.Scene);
        Assert.HasCount(0, fixture.Matching.SingleMatches);
        Assert.AreEqual(0, fixture.Matching.CollectionMatchCalls);
    }

    [TestMethod]
    public async Task WorldMatchUsesTheClientAreaAndExistingTemplateScaling()
    {
        using var fixture = new SceneFixture();
        fixture.Matching.MagicPoints = 1;
        using var borderedFrame = CapturedFrame.RentBgra32(108, 76);
        var state = new RuntimeTaskState(
            new CaptureTargetWindow
            {
                Hwnd = 1, Width = 108, Height = 76,
                ClientWidth = 96, ClientHeight = 54,
                WindowClientOffsetX = 6, WindowClientOffsetY = 22
            },
            fixture.State.RecognitionRegionConfig, fixture.State.Options, DateTimeOffset.Now);

        var result = await fixture.Recognizer.RecognizeAsync(state, borderedFrame, CancellationToken.None);

        Assert.AreEqual(GameScene.World, result.Scene);
        Assert.IsNotNull(fixture.Matching.CollectionRegion);
        Assert.AreEqual((11, 26, 10, 4), (
            fixture.Matching.CollectionRegion.X, fixture.Matching.CollectionRegion.Y,
            fixture.Matching.CollectionRegion.Width, fixture.Matching.CollectionRegion.Height));
        Assert.IsNotNull(fixture.Matching.CollectionOptions);
        Assert.AreEqual(1d, fixture.Matching.CollectionOptions.TemplateScaleX);
        Assert.AreEqual(1d, fixture.Matching.CollectionOptions.TemplateScaleY);
        Assert.AreEqual(6, fixture.Matching.MaximumMatches);
    }

    private sealed class SceneFixture : IDisposable
    {
        public MatchingStub Matching { get; } = new();
        public OverlayStub Overlay { get; } = new();
        public RuntimeTaskState State { get; }
        public GameSceneRecognizer Recognizer { get; }
        private readonly CapturedFrame _frame = CapturedFrame.RentBgra32(96, 54);

        public SceneFixture(bool autoBattleEnabled = true)
        {
            State = new(new CaptureTargetWindow { Hwnd = 1, ClientWidth = 96, ClientHeight = 54 },
                new RecognitionRegionConfig
                {
                    ResolutionWidth = 96, ResolutionHeight = 54,
                    Regions = [
                        Region("magic-point", 5, 4, 10, 4),
                        Region("battle-button-chat", 81, 30, 12, 12),
                        Region("battle-button-skill", 81, 42, 12, 12),
                        Region("battle-button-change", 65, 42, 12, 12)
                    ]
                },
                new RuntimeTaskStartOptions { AutoBattleSettings = new() { IsEnabled = autoBattleEnabled } },
                DateTimeOffset.Now);
            var debug = new RuntimeDebugLogger(NullLogger<RuntimeDebugLogger>.Instance);
            var frameRecognizer = new RuntimeFrameRecognizer(Matching, null!, Overlay, debug);
            Recognizer = new(frameRecognizer, Matching, Overlay, debug, new BattleScreenRecognizer(frameRecognizer));
            var templateDirectory = Path.Combine(Matching.TemplateDirectory, "96x54");
            Directory.CreateDirectory(templateDirectory);
            foreach (var template in new[] { "magic-point.png", "battle-chat.png", "battle-button-skill.png", "battle-button-change.png" })
            {
                File.WriteAllBytes(Path.Combine(templateDirectory, template), []);
            }
        }

        public Task<GameSceneMatch> RecognizeAsync() => Recognizer.RecognizeAsync(State, _frame, CancellationToken.None);
        private static RecognitionRegion Region(string id, int x, int y, int width, int height) => new()
        { Id = id, X = x, Y = y, Width = width, Height = height, Enabled = true };

        public void Dispose()
        {
            _frame.Dispose();
            Directory.Delete(Matching.TemplateDirectory, recursive: true);
        }
    }

    private sealed class MatchingStub : IImageMatchingService
    {
        public string TemplateDirectory { get; } = Path.Combine(Path.GetTempPath(), "RocoPilot.Tests", Guid.NewGuid().ToString("N"));
        public ImageMatchAlgorithm DefaultAlgorithm => ImageMatchAlgorithm.OpenCvSqDiffNormalized;
        public string? MatchedTemplate;
        public string? MatchedRegion;
        public int MagicPoints;
        public int CollectionMatchCalls;
        public int MaximumMatches;
        public RecognitionRegion? CollectionRegion;
        public ImageMatchOptions? CollectionOptions;
        public List<(string Template, string Region)> SingleMatches { get; } = [];

        public IReadOnlyList<string> ListTemplatePaths() => [];

        public Task<ImageMatchResult> MatchAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            ImageMatchOptions? options = null, CancellationToken cancellationToken = default)
        {
            var template = Path.GetFileName(templatePath);
            SingleMatches.Add((template, region.Id));
            var matched = template == MatchedTemplate && (MatchedRegion is null || region.Id == MatchedRegion);
            return Task.FromResult(new ImageMatchResult(matched, matched ? 0.99 : 0.4, 0, 0, 8, 8, templatePath));
        }

        public Task<ImageMatchCollectionResult> FindMatchesAsync(CapturedFrame frame, RecognitionRegion region,
            string templatePath, int maximumMatches, ImageMatchOptions? options = null,
            double maximumOverlapRatio = 0.5, CancellationToken cancellationToken = default)
        {
            CollectionMatchCalls++;
            CollectionRegion = region;
            CollectionOptions = options;
            MaximumMatches = maximumMatches;
            return Task.FromResult(new ImageMatchCollectionResult(
                Enumerable.Range(0, MagicPoints).Select(i => new ImageMatchResult(true, 0.98, i, 0, 1, 1, templatePath)).ToArray(),
                MagicPoints > 0 ? 0.98 : 0.4, templatePath));
        }
    }

    private sealed class OverlayStub : IRecognitionOverlayService
    {
        public List<(string Region, double Score)> Results { get; } = [];
        public void Show(RuntimeTaskState state) { }
        public void Hide() { }
        public void ShowOcrResult(string regionId, string text) => throw new AssertFailedException("场景识别不应调用 OCR。");
        public void ShowImageMatchResult(string regionId, double score) => Results.Add((regionId, score));
    }
}
