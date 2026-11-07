using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class LegendaryLocalScreenshotTests
{
    [TestMethod]
    [DataRow(960, 540)]
    [DataRow(1024, 576)]
    [DataRow(1280, 720)]
    [DataRow(1600, 900)]
    [DataRow(1920, 1080)]
    [DataRow(2048, 1152)]
    [DataRow(2560, 1440)]
    public async Task UnderfilledTeamConfirmsTheRightButtonAndYieldsToTheNextPrompt(int width, int height)
    {
        const string startTemplate = "2048x1152/legendary/challenge.png";
        var images = new ImageMatchingService();
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-local/multiplayer-underfilled-team.png",
            width, height, 12, 30, source =>
            {
                // 模拟遮罩后仍能命中的旧开始按钮。
                using var start = OpenCvSharp.Cv2.ImRead(Path.Combine(images.TemplateDirectory, startTemplate));
                using var area = new OpenCvSharp.Mat(source, new OpenCvSharp.Rect(1430, 1045, start.Width, start.Height));
                start.CopyTo(area);
            });
        var recognizer = new ManualChallengeScreenRecognizer(images, null!, null!, null!);
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
        Assert.AreEqual(ManualChallengeScene.UnderfilledTeam, screen.Scene);
        Assert.IsNotNull(screen.Button);
        var x = (screen.Button.X + screen.Button.Width / 2d - 12) / width;
        var y = (screen.Button.Y + screen.Button.Height / 2d - 30) / height;
        Assert.IsTrue(x is > .56 and < .61 && y is > .80 and < .85);
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.LegendaryConfirmation,
            startTemplate, 0, 12, 30, width, height, CancellationToken.None, legendary: true));
        Assert.AreEqual(screen.Button, await Recheck(frame));

        using var shortage = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-insufficient.png", width, height, 12, 30);
        Assert.IsNull(await Recheck(shortage));

        Task<RocoPilot.Models.ImageMatching.ImageMatchResult?> Recheck(RocoPilot.Models.Capture.CapturedFrame next) =>
            recognizer.FindClickTargetAsync(next, screen.Scene, screen.Button.TemplatePath, 0,
                12, 30, width, height, CancellationToken.None, legendary: true);
    }

    [TestMethod]
    [DataRow(2048, 1152)]
    [DataRow(1280, 720)]
    public async Task CaptureWithOnlyContractMarkCanConfirm(int width, int height)
    {
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-local/capture-contract-only.png",
            width, height, 12, 30);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), null!, null!, null!);
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
        Assert.AreEqual(ManualChallengeScene.CaptureConfirmation, screen.Scene);
        Assert.IsNotNull(screen.Marks);
        Assert.IsFalse(screen.Marks.LegendarySelected);
        Assert.IsNotNull(screen.Button);
        Assert.IsTrue(screen.Button.IsMatch);
        var confirmX = (screen.Button.X + screen.Button.Width / 2d - 12) / width;
        var confirmY = (screen.Button.Y + screen.Button.Height / 2d - 30) / height;
        Assert.IsTrue(confirmX is > .55 and < .61 && confirmY is > .79 and < .85);
        Assert.AreEqual(screen.Button, await recognizer.FindClickTargetAsync(frame, screen.Scene,
            screen.Button.TemplatePath, 0, 12, 30, width, height, CancellationToken.None, legendary: true));
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, screen.Scene, "legendary-select-contract",
            0, 12, 30, width, height, CancellationToken.None, legendary: true));
    }

    [TestMethod]
    [DataRow(2048, 1152)]
    [DataRow(1280, 720)]
    public async Task PreparationStartIsRecognizedWithoutCursor(int width, int height)
    {
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-local/preparation-start-stalled.png",
            width, height, 12, 30);
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), null!, null!, null!);
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
        Assert.AreEqual(ManualChallengeScene.Preparation, screen.Scene);
        Assert.IsNotNull(screen.Button);
        Assert.IsTrue(screen.Button.IsMatch);
        var centerX = (screen.Button.X + screen.Button.Width / 2d - 12) / width;
        var centerY = (screen.Button.Y + screen.Button.Height / 2d - 30) / height;
        Assert.IsTrue(centerX is > .55 and < .61 && centerY is > .79 and < .85);
        Assert.AreEqual(screen.Button, await recognizer.FindClickTargetAsync(frame, screen.Scene,
            screen.Button.TemplatePath, 0, 12, 30, width, height, CancellationToken.None, legendary: true));
    }

    [TestMethod]
    [DataRow("wolf-contract", false)]
    [DataRow("wolf-mark", true)]
    [DataRow("arena-contract", false)]
    [DataRow("arena-mark", true)]
    [DataRow("bones-contract", false)]
    [DataRow("bones-mark", true)]
    public async Task CaptureConfirmationRecognizesBothChoices(string sample, bool markSelected)
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), null!, null!, null!);
        foreach (var (width, height) in new[] { (2048, 1152), (1280, 720) })
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture($"legendary-local/{sample}.png",
                width, height, 12, 30);
            var diagnostic = $"{sample} {width}x{height}";
            var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
            Assert.AreEqual(ManualChallengeScene.CaptureConfirmation, screen.Scene, diagnostic);
            Assert.IsNotNull(screen.Marks, diagnostic);
            Assert.AreEqual(markSelected, screen.Marks.LegendarySelected, diagnostic);
            Assert.IsNotNull(screen.Button, diagnostic);
            Assert.IsTrue(screen.Button.IsMatch, diagnostic);
            var confirmX = (screen.Button.X + screen.Button.Width / 2d - 12) / width;
            var confirmY = (screen.Button.Y + screen.Button.Height / 2d - 30) / height;
            Assert.IsTrue(confirmX is > .52 and < .64 && confirmY is > .78 and < .87, diagnostic);
            var confirm = await recognizer.FindClickTargetAsync(frame, screen.Scene, screen.Button.TemplatePath,
                0, 12, 30, width, height, CancellationToken.None, legendary: true);
            Assert.AreEqual(screen.Button, confirm, diagnostic);
            foreach (var (target, completed) in new[]
            {
                ("legendary-select-contract", !markSelected), ("legendary-select-mark", markSelected)
            })
            {
                var button = await recognizer.FindClickTargetAsync(frame, screen.Scene, target,
                    0, 12, 30, width, height, CancellationToken.None, legendary: true);
                if (completed) Assert.IsNull(button, diagnostic);
                else
                {
                    Assert.IsNotNull(button, diagnostic);
                    Assert.IsTrue(button.IsMatch, diagnostic);
                    Assert.AreEqual(markSelected ? screen.Marks.ContractButton : screen.Marks.LegendaryButton, button, diagnostic);
                }
            }
        }
    }
}
