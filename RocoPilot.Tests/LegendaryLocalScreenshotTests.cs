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
    [DataRow("wolf-contract", false)]
    [DataRow("wolf-mark", true)]
    [DataRow("arena-contract", false)]
    [DataRow("arena-mark", true)]
    [DataRow("bones-contract", false)]
    [DataRow("bones-mark", true)]
    public async Task CaptureConfirmationRecognizesBothChoices(string sample, bool markSelected)
    {
        var recognizer = new ManualChallengeScreenRecognizer(new ImageMatchingService(), null!, null!, null!,
            NullLogger<ManualChallengeScreenRecognizer>.Instance);
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
