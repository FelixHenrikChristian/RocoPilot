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
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FlowerSeedScreenRecognizerTests
{
    [TestMethod]
    [DataRow("manual", nameof(ManualChallengeScene.Manual))]
    [DataRow("list", nameof(ManualChallengeScene.LegendaryList))]
    [DataRow("map", nameof(ManualChallengeScene.Map))]
    [DataRow("world", nameof(ManualChallengeScene.World))]
    [DataRow("skip", nameof(ManualChallengeScene.SkippableAnimation))]
    [DataRow("message", nameof(ManualChallengeScene.Interaction))]
    [DataRow("interaction-dark", nameof(ManualChallengeScene.Interaction))]
    [DataRow("interaction-gold", nameof(ManualChallengeScene.Interaction))]
    [DataRow("confirmation", nameof(ManualChallengeScene.LegendaryConfirmation))]
    [DataRow("difficulty-1", nameof(ManualChallengeScene.LegendaryConfirmation))]
    [DataRow("difficulty-2", nameof(ManualChallengeScene.LegendaryConfirmation))]
    [DataRow("difficulty-3", nameof(ManualChallengeScene.LegendaryConfirmation))]
    [DataRow("insufficient", nameof(ManualChallengeScene.InsufficientContractMark))]
    [DataRow("preparation", nameof(ManualChallengeScene.Preparation))]
    [DataRow("battle", nameof(ManualChallengeScene.Battle))]
    [DataRow("pet-switching", nameof(ManualChallengeScene.Battle))]
    [DataRow("pet-confirmation", nameof(ManualChallengeScene.Battle))]
    [DataRow("capture-contract", nameof(ManualChallengeScene.CaptureConfirmation))]
    [DataRow("capture-legendary", nameof(ManualChallengeScene.CaptureConfirmation))]
    [DataRow("rewards", nameof(ManualChallengeScene.LegendaryRewards))]
    [DataRow("capture", nameof(ManualChallengeScene.Capture))]
    [DataRow("result", nameof(ManualChallengeScene.Result))]
    public async Task LegendaryScreensUseRealTemplatesAndOcr(string fixture, string expected)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), ocr, null!, null!,
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        foreach (var (width, height) in new[] { (2048, 1152), (1280, 720) })
        {
            using var frame = LoadFixture($"legendary-{fixture}.png", width, height, 12, 30);
            List<string> texts = [];
            var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None,
                (_, text) => texts.Add(text), legendary: true);
            var diagnostic = $"{fixture} {width}x{height} OCR: {string.Join(" | ", texts)}";
            Assert.AreEqual(Enum.Parse<ManualChallengeScene>(expected), screen.Scene, diagnostic);
            if (fixture == "list") CollectionAssert.AreEqual(new[] { "圣羽翼王", "龙息帕尔" }, screen.Rows.Select(row => row.Name).ToArray(), diagnostic);
            else Assert.IsNotNull(screen.Button, diagnostic);
            if (fixture == "map") Assert.AreEqual("圣羽翼王", screen.Text, diagnostic);
            if (fixture.StartsWith("interaction")) Assert.AreEqual("挑战", screen.Text, diagnostic);
            if (fixture == "message") Assert.AreEqual("查看留言", screen.Text, diagnostic);
            if (screen.Scene == ManualChallengeScene.Interaction)
                Assert.AreEqual(fixture != "message", screen.HasChallengeOption, diagnostic);
            if (fixture == "battle") Assert.AreEqual(BattleScreen.SkillSelection, screen.BattleScreen, diagnostic);
            if (fixture == "pet-switching") Assert.AreEqual(BattleScreen.PetSwitching, screen.BattleScreen, diagnostic);
            if (fixture == "pet-confirmation") Assert.AreEqual(BattleScreen.PetSwitchConfirmation, screen.BattleScreen, diagnostic);
            if (fixture == "confirmation") Assert.HasCount(0, texts, diagnostic);
            if (screen.Scene == ManualChallengeScene.LegendaryConfirmation)
            {
                var selected = fixture == "confirmation" ? 3 : int.Parse(fixture[^1..]);
                Assert.AreEqual(selected, screen.Difficulty, diagnostic);
                CollectionAssert.AreEqual(Enumerable.Range(1, 3).Where(number => number != selected).ToArray(),
                    screen.Rows.Select(row => row.Number).ToArray(), diagnostic);
                foreach (var row in screen.Rows)
                {
                    Assert.IsNotNull(await recognizer.FindClickTargetAsync(frame, screen.Scene, row.Button.TemplatePath,
                        0, 12, 30, width, height, CancellationToken.None, legendary: true), diagnostic);
                    using var switched = LoadFixture($"legendary-difficulty-{row.Number}.png", width, height, 12, 30);
                    Assert.IsNull(await recognizer.FindClickTargetAsync(switched, screen.Scene, row.Button.TemplatePath,
                        0, 12, 30, width, height, CancellationToken.None, legendary: true), diagnostic);
                }
            }
            if (screen.Marks is { } marks)
            {
                Assert.HasCount(0, texts, diagnostic);
                Assert.AreEqual(fixture == "capture-legendary", marks.LegendarySelected, diagnostic);
            }
            if (screen.Scene is ManualChallengeScene.LegendaryConfirmation or ManualChallengeScene.InsufficientContractMark
                or ManualChallengeScene.Preparation or ManualChallengeScene.Map or ManualChallengeScene.Result or ManualChallengeScene.CaptureConfirmation)
            {
                var rechecked = await recognizer.FindClickTargetAsync(frame, screen.Scene, screen.Button!.TemplatePath,
                    0, 12, 30, width, height, CancellationToken.None, legendary: true);
                Assert.IsNotNull(rechecked, diagnostic);
                Assert.AreEqual((screen.Button.X, screen.Button.Y), (rechecked.X, rechecked.Y), diagnostic);
            }
        }
    }

    [TestMethod]
    [DataRow("gold", -72, 2048, 1152)]
    [DataRow("dark", 72, 2048, 1152)]
    [DataRow("gold", 72, 1280, 720)]
    [DataRow("dark", -72, 1280, 720)]
    public async Task ChallengeInAnotherInteractionRowIsRecognizedWithoutSelectingTheMessage(
        string color, int offsetY, int width, int height)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(),
            new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]), null!, null!,
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture("legendary-message.png", width, height, 12, 30);
        using var challenge = LoadFixture($"legendary-interaction-{color}.png", width, height, 12, 30);
        var scale = height / 1152d;
        var x = 12 + (int)Math.Round(1217 * scale);
        var y = 30 + (int)Math.Round(582 * scale);
        var rowBytes = (int)Math.Round(282 * scale) * 4;
        // 用真实截图中的挑战行构造上下两种排列，保留留言行旁的 F 选中标记。
        for (var row = 0; row < (int)Math.Round(48 * scale); row++)
            Array.Copy(challenge.Pixels, ((y + row) * frame.Width + x) * 4,
                frame.Pixels, ((y + row + (int)Math.Round(offsetY * scale)) * frame.Width + x) * 4, rowBytes);

        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);

        Assert.AreEqual(ManualChallengeScene.Interaction, screen.Scene);
        Assert.AreEqual("查看留言", screen.Text);
        Assert.IsTrue(screen.HasChallengeOption);
    }

    [TestMethod]
    [DataRow(0, 2048, 1152)]
    [DataRow(1, 2048, 1152)]
    [DataRow(2, 2048, 1152)]
    [DataRow(0, 1280, 720)]
    [DataRow(1, 1280, 720)]
    [DataRow(2, 1280, 720)]
    public async Task SkipRequiresBothControlsAndIgnoresChangingBackground(int backgroundKind, int width, int height)
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr(),
            new Catalog(), new Seasons(.7), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        // 0：没有控件；1：只有图标；2：只有 Esc；3：完整跳过控件。
        for (var controls = 0; controls <= 3; controls++)
        {
            using var frame = LoadFixture("legendary-skip.png", width, height, 12, 30, source =>
            {
                using var background = new Mat(source.Size(), source.Type(), backgroundKind == 0 ? Scalar.Black : Scalar.White);
                var sourceWidth = source.Width;
                var sourceHeight = source.Height;
                if (backgroundKind == 2)
                    for (var y = 0; y < sourceHeight; y += 16)
                        for (var x = 0; x < sourceWidth; x += 16)
                            Cv2.Rectangle(background, new Rect(x, y, 16, 16),
                                (x / 16 + y / 16) % 2 == 0 ? new Scalar(30, 210, 80) : new Scalar(240, 60, 180), -1);
                using var mask = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
                if ((controls & 1) != 0) Cv2.Circle(mask, new Point(154, 60), 26, Scalar.White, -1);
                if ((controls & 2) != 0) Cv2.Ellipse(mask, new Point(154, 105), new Size(26, 12), 0, 0, 360, Scalar.White, -1);
                source.CopyTo(background, mask);
                background.CopyTo(source);
            });

            var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);

            Assert.AreEqual(controls == 3, screen.Scene == ManualChallengeScene.SkippableAnimation,
                $"背景 {backgroundKind}，控件 {controls}，分辨率 {width}x{height}");
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task DifficultyRecognitionDoesNotDependOnBubbleOutline(int difficulty)
    {
        using var frame = LoadFixture($"legendary-difficulty-{difficulty}.png", 1280, 720, 12, 30, source =>
        {
            foreach (var y in new[] { 174, 283, 393 })
            {
                using var centerArea = new Mat(source, new Rect(149, y, 40, 40));
                using var center = centerArea.Clone();
                Cv2.Rectangle(source, new Rect(115, y - 30, 110, 100), new Scalar(80, 40, 170), -1);
                using var destination = new Mat(source, new Rect(149, y, 40, 40));
                center.CopyTo(destination);
            }
        });
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr(), null!, null!,
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        var screen = await recognizer.ReadAsync(frame, 12, 30, 1280, 720, CancellationToken.None, legendary: true);
        Assert.AreEqual(difficulty, screen.Difficulty);
        Assert.AreEqual(2, screen.Rows.Count);
    }

    [TestMethod]
    public async Task LegendaryPetHealthTemplateDoesNotRequireFullHealth()
    {
        using var frame = LoadFixture("legendary-pet-switching.png");
        // 清空第一只候选精灵血条的填充，保留 UI 外框。
        for (var y = 387; y <= 393; y++)
            for (var x = 330; x < 402; x++)
            {
                var offset = (y * frame.Width + x) * 4;
                frame.Pixels[offset] = frame.Pixels[offset + 1] = frame.Pixels[offset + 2] = 30;
            }
        var match = await new ImageMatchingService().MatchAsync(frame,
            new RecognitionRegion { X = 320, Y = 375, Width = 100, Height = 32 },
            "2048x1152/legendary/pet-health.png", new ImageMatchOptions { MinimumScore = .94 });
        Assert.IsTrue(match.IsMatch, $"血条外框匹配分数：{match.Score}");
    }

    [TestMethod]
    [DataRow(1920, 1080, 0, 0)]
    [DataRow(2560, 1080, 12, 30)]
    [DataRow(1920, 1440, 12, 30)]
    public async Task ManualUsesActualNavigationMatchAndRechecksIt(int width, int height, int clientX, int clientY)
    {
        // 宽屏/高屏用同一张真实截图贴在左上，验证锚定和边框偏移，不模拟游戏的其他布局。
        using var source = LoadFixture("flower-seed-manual-1080p.png");
        using var frame = CreateFrame(width, height, clientX, clientY);
        for (var y = 0; y < source.Height; y++)
            Array.Copy(source.Pixels, y * source.Width * 4, frame.Pixels,
                ((y + clientY) * frame.Width + clientX) * 4, source.Width * 4);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr(),
            new Catalog(), new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);

        var screen = await recognizer.ReadAsync(frame, clientX, clientY, width, height, CancellationToken.None);

        Assert.AreEqual(ManualChallengeScene.Manual, screen.Scene);
        Assert.IsNotNull(screen.Button);
        var button = screen.Button;
        Assert.IsTrue(button.X + button.Width / 2 - clientX is >= 140 and <= 180);
        Assert.IsTrue(button.Y + button.Height / 2 - clientY is >= 270 and <= 306);
        Assert.IsNotNull(await recognizer.FindClickTargetAsync(frame, screen.Scene, button.TemplatePath, 0,
            clientX, clientY, width, height, CancellationToken.None));
        for (var y = button.Y; y < button.Y + button.Height; y++)
            Array.Clear(frame.Pixels, (y * frame.Width + button.X) * 4, button.Width * 4);
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, screen.Scene, button.TemplatePath, 0,
            clientX, clientY, width, height, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(1280, 720)]
    [DataRow(2560, 1440)]
    public async Task ScaledManualScreenshotLocatesNavigation(int width, int height)
    {
        using var frame = LoadFixture("flower-seed-manual-1080p.png", width, height, 12, 30);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr(),
            new Catalog(), new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None);
        Assert.AreEqual(ManualChallengeScene.Manual, screen.Scene);
        Assert.IsNotNull(screen.Button);
        var x = (screen.Button.X + screen.Button.Width / 2d - 12) * 1920 / width;
        var y = (screen.Button.Y + screen.Button.Height / 2d - 30) * 1080 / height;
        Assert.IsTrue(x is >= 140 and <= 180 && y is >= 270 and <= 306);
    }

    [TestMethod]
    public async Task ManualHeaderNeedsSmallScaleAdjustmentOnActual1080pScreenshot()
    {
        using var frame = LoadFixture("flower-seed-manual-1080p.png");
        var images = new ImageMatchingService();
        var area = new RecognitionRegion { X = 154, Y = 5, Width = 230, Height = 59 };
        var options = new ImageMatchOptions
        {
            TemplateScaleX = .9375, TemplateScaleY = .9375, MinimumScore = .9, ScaleTolerance = 0
        };
        var fixedScale = await images.MatchAsync(frame, area, "2048x1152/flower-seed/manual-header.png", options);
        Assert.IsFalse(fixedScale.IsMatch, $"固定比例：{fixedScale.Score:F3}");

        options.ScaleTolerance = .04;
        var adjusted = await images.MatchAsync(frame, area, "2048x1152/flower-seed/manual-header.png", options);
        Assert.IsTrue(adjusted.IsMatch, $"自适应比例：{adjusted.Score:F3}");
        Assert.IsGreaterThan(.94, adjusted.Score);
        Assert.AreEqual((187, 19, 119, 26), (adjusted.X, adjusted.Y, adjusted.Width, adjusted.Height));
    }

    [TestMethod]
    public async Task SelectedTabEndsClickingEvenThoughTheTabTemplateStillMatches()
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ListImages(2048, 1152, 0, 0), new NameOcr(),
            new Catalog(), new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = CreateFrame(2048, 1152, 0, 0);
        Array.Clear(frame.Pixels);
        var tab = await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.ChallengePage, "flower-tab", 0,
            0, 0, 2048, 1152, CancellationToken.None);
        Assert.IsNotNull(tab);

        Array.Fill(frame.Pixels, (byte)245);

        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.ChallengePage, "flower-tab", 0,
            0, 0, 2048, 1152, CancellationToken.None));
    }

    [TestMethod]
    public async Task RewardClickEndsWhenItsOcrTitleDisappears()
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr("获得奖励", "", ""),
            new Catalog(), new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = CreateFrame(2048, 1152, 0, 0);

        Assert.IsNotNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.Rewards, "flower-rewards-dismiss", 0,
            0, 0, 2048, 1152, CancellationToken.None));
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.Rewards, "flower-rewards-dismiss", 0,
            0, 0, 2048, 1152, CancellationToken.None));
    }

    [TestMethod]
    public async Task DisappearedListButtonCannotBeReplacedByAnIdenticalButtonInAnotherRow()
    {
        var images = new ImageMatchingService();
        var recognizer = new ManualChallengeScreenRecognizer(images, new NameOcr(), new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var template = Cv2.ImRead(Path.Combine(images.TemplateDirectory, "2048x1152/flower-seed/list-teleport.png"), ImreadModes.Unchanged);
        Assert.IsFalse(template.Empty());
        var templateWidth = template.Width;
        var templateHeight = template.Height;
        using var canvas = new Mat(1152, 2048, MatType.CV_8UC4, Scalar.All(230));
        foreach (var y in new[] { 280, 510 })
        {
            using var region = new Mat(canvas, new Rect(1700, y, templateWidth, templateHeight));
            template.CopyTo(region);
        }
        var pixels = new byte[2048 * 1152 * 4];
        System.Runtime.InteropServices.Marshal.Copy(canvas.Data, pixels, 0, pixels.Length);
        using var frame = new CapturedFrame(2048, 1152, pixels);
        var rowY = (280 + templateHeight / 2d) / 1152;
        var first = await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.FlowerList, "list-teleport.png", rowY,
            0, 0, 2048, 1152, CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.AreEqual(280, first.Y);

        for (var y = 280; y < 280 + templateHeight; y++)
            Array.Clear(pixels, (y * 2048 + 1700) * 4, templateWidth * 4);

        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.FlowerList, "list-teleport.png", rowY,
            0, 0, 2048, 1152, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("flower-seed-map.png")]
    [DataRow("flower-seed-confirmation.png")]
    [DataRow("flower-seed-preparation.png")]
    [DataRow("flower-seed-medal-confirmation.png")]
    [DataRow("flower-seed-result.png")]
    public async Task ClickTargetIsRecheckedUntilItsTemplateDisappears(string fixture)
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr(""),
            new Catalog(), new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        var map = fixture == "flower-seed-map.png"; // 地图样本含原游戏窗口边框。
        var clientX = map ? 1 : 12;
        var width = map ? 1366 : 1280;
        var height = map ? 768 : 720;
        using var frame = map ? LoadFixture(fixture) : LoadFixture(fixture, width, height, clientX, 30);
        var screen = await recognizer.ReadAsync(frame, clientX, 30, width, height, CancellationToken.None);
        Assert.IsNotNull(screen.Button);
        var original = screen.Button;

        var found = await recognizer.FindClickTargetAsync(frame, screen.Scene, original.TemplatePath, 0,
            clientX, 30, width, height, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual((original.X, original.Y), (found.X, found.Y));
        for (var y = original.Y; y < original.Y + original.Height; y++)
            Array.Clear(frame.Pixels, (y * frame.Width + original.X) * 4, original.Width * 4);

        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, screen.Scene, original.TemplatePath, 0,
            clientX, 30, width, height, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("flower-seed-battle.png", nameof(ManualChallengeScene.Battle), BattleScreen.SkillSelection)]
    [DataRow("flower-seed-battle-detail.png", nameof(ManualChallengeScene.Battle), BattleScreen.SkillSelection)]
    [DataRow("flower-seed-capture.png", nameof(ManualChallengeScene.Capture), null)]
    [DataRow("flower-seed-capture-duck.png", nameof(ManualChallengeScene.Capture), null)]
    [DataRow("flower-seed-rewards.png", nameof(ManualChallengeScene.Rewards), null)]
    [DataRow("flower-seed-rewards-single-row.png", nameof(ManualChallengeScene.Rewards), null)]
    [DataRow("flower-seed-result.png", nameof(ManualChallengeScene.Result), null)]
    [DataRow("flower-seed-fated-confirmation.png", nameof(ManualChallengeScene.Confirmation), null)]
    [DataRow("flower-seed-preparation.png", nameof(ManualChallengeScene.Preparation), null)]
    [DataRow("flower-seed-medal-confirmation.png", nameof(ManualChallengeScene.MedalConfirmation), null)]
    [DataRow("flower-seed-failure-world.png", nameof(ManualChallengeScene.Interaction), null)]
    public async Task RealChallengeScreensRecognizeTheirStageAndActionButtons(
        string fixture, string expectedScene, BattleScreen? expectedBattleScreen)
    {
        var ocr = new NameOcr(expectedScene == nameof(ManualChallengeScene.Rewards) ? "获得奖励" : "挑战!");
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture(fixture);

        var screen = await recognizer.ReadAsync(frame, 0, 0, frame.Width, frame.Height, CancellationToken.None);

        Assert.AreEqual(Enum.Parse<ManualChallengeScene>(expectedScene), screen.Scene);
        Assert.AreEqual(expectedBattleScreen, screen.BattleScreen);
        if (screen.Scene is ManualChallengeScene.Capture or ManualChallengeScene.Rewards or ManualChallengeScene.Result
            or ManualChallengeScene.Confirmation or ManualChallengeScene.Preparation or ManualChallengeScene.MedalConfirmation)
            Assert.IsTrue(screen.Button is { IsMatch: true });
        if (screen.Scene == ManualChallengeScene.Result) Assert.IsTrue(screen.ExitButton is { IsMatch: true });
        if (screen.Scene == ManualChallengeScene.Interaction)
        {
            Assert.AreEqual("挑战", screen.Text);
            Assert.HasCount(1, ocr.Calls);
            var region = ocr.Calls[0].Region;
            Assert.AreEqual((1265, 589, 150, 40), (region.X, region.Y, region.Width, region.Height));
        }
        else Assert.HasCount(screen.Scene == ManualChallengeScene.Rewards ? 1 : 0, ocr.Calls);
    }

    [TestMethod]
    [DataRow("flower-seed-capture.png", nameof(ManualChallengeScene.Capture))]
    [DataRow("flower-seed-capture-duck.png", nameof(ManualChallengeScene.Capture))]
    [DataRow("flower-seed-rewards.png", nameof(ManualChallengeScene.Rewards))]
    [DataRow("flower-seed-rewards-single-row.png", nameof(ManualChallengeScene.Rewards))]
    [DataRow("flower-seed-result.png", nameof(ManualChallengeScene.Result))]
    [DataRow("flower-seed-medal-confirmation.png", nameof(ManualChallengeScene.MedalConfirmation))]
    public async Task ChallengeStagesUseScaledTemplatesAndClientCoordinates(string fixture, string expectedScene)
    {
        var images = new ImageMatchingService();
        var recognizer = new ManualChallengeScreenRecognizer(images, new NameOcr("获得奖励"), new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture(fixture, 1280, 720, 12, 30);

        var screen = await recognizer.ReadAsync(frame, 12, 30, 1280, 720, CancellationToken.None);

        Assert.AreEqual(Enum.Parse<ManualChallengeScene>(expectedScene), screen.Scene);
        Assert.IsNotNull(screen.Button);
        Assert.IsTrue(screen.Button.X >= 12 && screen.Button.Y >= 30);
        Assert.IsTrue(screen.Button.X + screen.Button.Width <= 1292 && screen.Button.Y + screen.Button.Height <= 750);
        if (screen.Scene == ManualChallengeScene.Rewards) Assert.AreEqual((652, 678), (screen.Button.X, screen.Button.Y));
    }

    [TestMethod]
    [DataRow("flower-seed-rewards.png", 1280, 720)]
    [DataRow("flower-seed-rewards.png", 1600, 900)]
    [DataRow("flower-seed-rewards.png", 2048, 1152)]
    [DataRow("flower-seed-rewards-single-row.png", 1280, 720)]
    [DataRow("flower-seed-rewards-single-row.png", 1600, 900)]
    [DataRow("flower-seed-rewards-single-row.png", 2048, 1152)]
    public async Task RealOcrConfirmsBothRewardLayoutsAtDifferentResolutions(string fixture, int width, int height)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture(fixture, width, height, 12, 30);
        List<(RecognitionRegion Region, string Text)> previews = [];

        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None,
            (region, text) => previews.Add((region, text)));

        Assert.AreEqual(ManualChallengeScene.Rewards, screen.Scene);
        Assert.AreEqual("获得奖励", ManualChallengeScreenRecognizer.Normalize(screen.Text));
        Assert.HasCount(1, previews);
        Assert.AreEqual("花种奖励标题", previews[0].Region.Id);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SnowBattleBackgroundCannotBecomeRewardsWhenBattleControlsDisappear(bool hideBattleControls)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture("flower-seed-battle-snow.png");
        if (hideBattleControls)
            for (var y = 589; y < frame.Height; y++) Array.Clear(frame.Pixels, (y * frame.Width + 1089) * 4, (frame.Width - 1089) * 4);

        var screen = await recognizer.ReadAsync(frame, 1, 31, 1600, 900, CancellationToken.None);

        Assert.AreEqual(hideBattleControls ? ManualChallengeScene.Unknown : ManualChallengeScene.Battle, screen.Scene);
        if (!hideBattleControls) Assert.AreEqual(BattleScreen.SkillSelection, screen.BattleScreen);
    }

    [TestMethod]
    [DataRow("flower-seed-capture.png", 250, 290, 140, 60, nameof(ManualChallengeScene.Capture))]
    [DataRow("flower-seed-capture.png", 1880, 1000, 160, 150, nameof(ManualChallengeScene.Capture))]
    [DataRow("flower-seed-result.png", 830, 1040, 120, 90, nameof(ManualChallengeScene.Result))]
    [DataRow("flower-seed-result.png", 1080, 1040, 180, 90, nameof(ManualChallengeScene.Result))]
    public async Task CaptureAndResultRequireBothStageMarkers(
        string fixture, int x, int y, int width, int height, string excludedScene)
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), new NameOcr(""), new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture(fixture);
        for (var row = y; row < y + height; row++) Array.Clear(frame.Pixels, (row * frame.Width + x) * 4, width * 4);

        var screen = await recognizer.ReadAsync(frame, 0, 0, frame.Width, frame.Height, CancellationToken.None);

        Assert.AreNotEqual(Enum.Parse<ManualChallengeScene>(excludedScene), screen.Scene);
    }

    [TestMethod]
    public async Task InteractionMarkerWithoutWorldHudCannotEndTheBattle()
    {
        var ocr = new NameOcr("挑战!");
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture("flower-seed-failure-world.png");
        // 只保留交互菜单特征，模拟击败动画中偶然命中 F 与浅色选项的画面。
        for (var y = 23; y < 104; y++) Array.Clear(frame.Pixels, (y * frame.Width + 164) * 4, 246 * 4);

        var screen = await recognizer.ReadAsync(frame, 0, 0, frame.Width, frame.Height, CancellationToken.None);

        Assert.AreEqual(ManualChallengeScene.Unknown, screen.Scene);
        Assert.HasCount(2, ocr.Calls);
    }

    [TestMethod]
    public async Task BattleChatAloneDoesNotPermitSkillInput()
    {
        var images = new ImageMatchingService();
        var recognizer = new ManualChallengeScreenRecognizer(images, new NameOcr(""), new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = LoadFixture("flower-seed-battle.png");
        var originalChat = await images.MatchAsync(frame, new RecognitionRegion { X = 1393, Y = 714, Width = 655, Height = 438 },
            "2048x1152/battle-chat.png", new ImageMatchOptions { MinimumScore = .94 });
        for (var row = 970; row < 1152; row++) Array.Clear(frame.Pixels, (row * frame.Width + 1600) * 4, 448 * 4);

        var screen = await recognizer.ReadAsync(frame, 0, 0, frame.Width, frame.Height, CancellationToken.None);

        var chat = await images.MatchAsync(frame, new RecognitionRegion { X = 1393, Y = 714, Width = 655, Height = 438 },
            "2048x1152/battle-chat.png", new ImageMatchOptions { MinimumScore = .94 });
        var tightChat = await images.MatchAsync(frame, new RecognitionRegion { X = 1918, Y = 728, Width = 105, Height = 105 },
            "2048x1152/battle-chat.png", new ImageMatchOptions { MinimumScore = .94 });
        Assert.IsTrue(originalChat.IsMatch && tightChat.IsMatch);
        Assert.AreEqual(ManualChallengeScene.Battle, screen.Scene,
            $"聊天模板：原图 {originalChat.Score:F6} @ ({originalChat.X}, {originalChat.Y})；清除技能后 {chat.Score:F6} @ ({chat.X}, {chat.Y})；聊天区 {tightChat.Score:F6} @ ({tightChat.X}, {tightChat.Y})");
        Assert.AreEqual(BattleScreen.Chat, screen.BattleScreen);
    }

    [TestMethod]
    public async Task RealRareFlowerConfirmationUsesSharedHeaderAndFindsChallenge()
    {
        var images = new ImageMatchingService();
        var ocr = new NameOcr();
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
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

        Assert.AreEqual(ManualChallengeScene.Confirmation, screen.Scene);
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
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
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

        Assert.AreEqual(ManualChallengeScene.Map, screen.Scene);
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
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, catalog, new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = CreateFrame(2048, 1152, 0, 0);

        var first = await recognizer.ReadAsync(frame, 0, 0, 2048, 1152, CancellationToken.None);
        Assert.AreEqual(ManualChallengeScene.FlowerList, first.Scene);
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
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, catalog, seasons, NullLogger<ManualChallengeScreenRecognizer>.Instance);
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
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
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
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70),
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
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
        var recognizer = new ManualChallengeScreenRecognizer(images, ocr, new Catalog(), new Seasons(.70), NullLogger<ManualChallengeScreenRecognizer>.Instance);
        using var frame = CreateFrame(width, height, clientX, clientY);

        var screen = await recognizer.ReadAsync(frame, clientX, clientY, width, height, CancellationToken.None);

        Assert.AreEqual(ManualChallengeScene.FlowerList, screen.Scene);
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

    internal static CapturedFrame LoadFixture(string fileName, int width = 0, int height = 0, int clientX = 0, int clientY = 0,
        Action<Mat>? transform = null)
    {
        using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName), ImreadModes.Color);
        Assert.IsFalse(source.Empty(), $"未读取到测试样本：{fileName}");
        transform?.Invoke(source);
        if (width > 0 && height > 0) Cv2.Resize(source, source, new Size(width, height), interpolation: InterpolationFlags.Area);
        using var image = new Mat();
        Cv2.CvtColor(source, image, ColorConversionCodes.BGR2BGRA);
        var imageWidth = image.Width;
        var imageHeight = image.Height;
        var frameWidth = imageWidth + clientX;
        var frameHeight = imageHeight + clientY;
        var pixels = new byte[frameWidth * frameHeight * 4];
        for (var row = 0; row < imageHeight; row++)
            System.Runtime.InteropServices.Marshal.Copy(image.Ptr(row), pixels,
                ((row + clientY) * frameWidth + clientX) * 4, imageWidth * 4);
        return new(frameWidth, frameHeight, pixels);
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
            return Task.FromResult(new TextRecognitionResult(method, "ONNX", null, [_names.TryDequeue(out var text) ? text : string.Empty], 1));
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
