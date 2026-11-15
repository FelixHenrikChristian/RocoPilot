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

    [TestMethod]
    [DataRow(2048)]
    [DataRow(1280)]
    [DataRow(960)]
    public async Task PocketIdentifiesVisibleSeedsQuantityAndEquippedState(int width)
    {
        var images = new ImageMatchingService();
        foreach (var (file, top, quantity, equipped) in new[]
        {
            ("top-twenty", true, 3, false), ("bottom-four", false, 3, false),
            ("equipped-three", false, 3, true), ("unequipped-one", false, 1, false),
            ("unequipped-two", false, 2, false), ("unequipped-three", false, 3, false)
        })
        {
            var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30 };
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(
                Path.Combine(Folder(), "LocalScreenshots", "2026-11-16", "seed-pocket", file + ".png"), width, window.ClientHeight, 12, 30);
            var screen = new DailyTaskScreen(frame, window, images);
            var pocket = new GardenSeedPocket(frame, screen);
            Assert.IsNotNull(await pocket.Title(default));
            Assert.AreEqual(equipped, await pocket.Remove(default) != null);
            Assert.AreEqual(!equipped, await pocket.Equip(default) != null);
            for (var n = 1; n <= 3; n++) Assert.AreEqual(n == quantity, pocket.QuantitySelected(n));
            if (file is not ("top-twenty" or "bottom-four")) continue;
            for (var index = 0; index < 24; index++)
            {
                Assert.AreEqual(index == 22, await pocket.Detail(index, default) != null, $"详情 {file} {width} {index}");
                var match = await pocket.Seed(index, default);
                var visible = top ? index < 20 : index >= 5;
                Assert.AreEqual(visible, match != null, $"列表 {file} {width} {index}");
                if (match == null) continue;
                var y = top ? 399 + index / 5 * 138 : 430 + (index / 5 - 1) * 138;
                Assert.AreEqual(y, screen.ClientY(match.Y + match.Height / 2d) / screen.Scale, 9, $"位置 {index}");
            }
        }
    }

    [TestMethod]
    public async Task WorldAfterEquipIsRecognizedWithPocketClosed()
    {
        var window = new CaptureTargetWindow { ClientWidth = 2048, ClientHeight = 1152 };
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(
            Path.Combine(Folder(), "LocalScreenshots", "2026-11-16", "seed-pocket", "world-after-equip.png"), 2048, 1152, 0, 0);
        var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
        Assert.IsNull(await new GardenSeedPocket(frame, screen).Title(default));
        Assert.IsNotNull(await screen.World(default));
    }

    private static (CapturedFrame, CaptureTargetWindow) Load(string date, string name, int width)
    {
        var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = width * 9 / 16, ClientOffsetX = 12, ClientOffsetY = 30 };
        var path = Path.Combine(Folder(), "LocalScreenshots", date, "gardening", name + ".png");
        return (FlowerSeedScreenRecognizerTests.LoadFixture(path, width, window.ClientHeight, 12, 30), window);
    }
}
