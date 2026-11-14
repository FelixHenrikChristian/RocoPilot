using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Capture;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class GardeningTests
{
    private static string Folder([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    [TestMethod]
    public void NorthwardMovementReducesRowFromTheEntrance()
    {
        Assert.AreEqual("W", new GardenPosition(2.5, 4.3).DirectionTo(2.5, 2.5));
        Assert.IsNull(new GardenPosition(2.5, 2.7).DirectionTo(2.5, 2.5));
        Assert.AreEqual("S", new GardenPosition(2.5, 2.5).DirectionTo(2.5, 4.3));
    }

    [TestMethod]
    [DataRow("2026-11-09", "selected-corner", 0, 2, true)]
    [DataRow("2026-11-09", "empty-field", 2, 2, true)]
    [DataRow("2026-11-14", "planted", 4, 0, false)]
    public async Task LocatesPlotAndRecognizesPlantAction(string date, string name, int column, int row, bool empty)
    {
        foreach (var width in new[] { 2048, 1280, 960 })
        {
            var (frame, window) = Load(date, name, width);
            using (frame)
            {
                var pos = new GardenVision(frame, window).Locate();
                Assert.IsNotNull(pos);
                Assert.AreEqual(column, (int)pos.Value.Column);
                Assert.AreEqual(row, (int)pos.Value.Row);
                var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
                Assert.AreEqual(empty, await screen.CanPlant(default));
                Assert.IsNotNull(await screen.World(default));
                if (!empty)
                    Assert.IsTrue(await screen.Find("gardening/shovel", 1610, 950, 1850, 1130, default, .90) != null ||
                        await screen.Find("gardening/growth", 775, 160, 1240, 555, default) != null);
            }
        }
    }

    [TestMethod]
    [DataRow("2026-11-14", "care-pending", false, true)]
    [DataRow("2026-11-14", "care-finished", false, false)]
    [DataRow("2026-11-15", "care-already-finished", false, false)]
    [DataRow("2026-11-09", "empty-field", true, false)]
    [DataRow("2026-11-09", "ripe-field", true, true)]
    public async Task ChecksFieldBubbles(string date, string name, bool harvest, bool expected)
    {
        foreach (var width in new[] { 2048, 1280, 960 })
        {
            var (frame, window) = Load(date, name, width);
            using (frame)
            {
                var found = harvest
                    ? await new DailyTaskScreen(frame, window, new ImageMatchingService()).HasHarvestPrompt(default)
                    : new GardenVision(frame, window).HasCareBubble();
                Assert.AreEqual(expected, found, $"{width}");
            }
        }
    }

    [TestMethod]
    public async Task PetPanelDoesNotOfferPlanting()
    {
        var (frame, window) = Load("2026-11-14", "pet-panel", 1280);
        using (frame)
        {
            var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
            Assert.IsNotNull(await screen.Find("gardening/pet-panel", 1650, 0, 1980, 145, default, .89));
            Assert.IsFalse(await screen.CanPlant(default));
        }
    }

    private static (CapturedFrame, CaptureTargetWindow) Load(string date, string name, int width)
    {
        var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30 };
        var path = Path.Combine(Folder(), "LocalScreenshots", date, "gardening", name + ".png");
        return (FlowerSeedScreenRecognizerTests.LoadFixture(path, width, window.ClientHeight, 12, 30), window);
    }
}
