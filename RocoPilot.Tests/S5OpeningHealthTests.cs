using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.Recognition;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Models.Capture;

namespace RocoPilot.Tests;

[TestClass]
public sealed class S5OpeningHealthTests
{
    [TestMethod]
    public void RequiresTwoConsecutiveOpeningObservationsWithoutInventingDisabledState()
    {
        var tracker = new EncounterCaptureButtonStateTracker();
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled, openingEvidence: true));
        Assert.IsTrue(tracker.ShouldHoldAttackForUnconfirmedRelief);
        Assert.IsFalse(tracker.HasSeenDisabled);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled));
        Assert.IsFalse(tracker.ShouldHoldAttackForUnconfirmedRelief);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled, openingEvidence: true));
        Assert.IsTrue(tracker.Observe(EncounterCaptureButtonState.Enabled, openingEvidence: true));
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled, openingEvidence: true));
        tracker.Reset();
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled));
    }

    [TestMethod, TestCategory("LocalScreenshots")]
    [DataRow("opening-1", true)]
    [DataRow("opening-2", true)]
    [DataRow("opening-3", true)]
    [DataRow("low", false)]
    [DataRow("full-synthetic", false)]
    public async Task MatchesOnlyOpeningTenPercent(string file, bool expected)
    {
        foreach (var width in new[] { 960, 1024, 1280, 1536, 1600, 1920, 2048, 2560 })
        {
            var height = width * 9 / 16;
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(FixtureFolder(), file + ".png"),
                width, height, clientX: 12, clientY: 30);
            var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = height, ClientOffsetX = 12, ClientOffsetY = 30 };
            var config = new RecognitionRegionConfigService(NullLogger<RecognitionRegionConfigService>.Instance)
                .LoadForResolution(width, height);
            var region = RuntimeFrameRecognizer.FindRegion(config, ["s5-opening-ten-percent"]);
            var result = await new ImageMatchingService().MatchAsync(frame,
                RecognitionRegionImageHelper.ToFrameRegion(region, frame, window, config),
                RuntimeFrameRecognizer.GetResolutionTemplatePath(config, "s5-opening-ten-percent.png"),
                RuntimeFrameRecognizer.CreateScaledImageMatchOptions(RuntimeTaskService.S5OpeningHealthOptions, frame, window, config));
            Assert.AreEqual(expected, result.IsMatch, $"{file} {width}px: {result.Score:F4}");
        }
    }

    private static string FixtureFolder([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "s5-opening-health");
}
