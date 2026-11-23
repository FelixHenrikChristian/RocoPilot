using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Capture;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class BattlePassTests
{
    [TestMethod]
    [DataRow(2048)]
    [DataRow(1280)]
    [DataRow(960)]
    public async Task RecognizesLevelRewardsButNotItsPopup(int width)
    {
        var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        foreach (var name in new[] { "level-rewards-window-client", "level-rewards-popup-window-client", "tasks-three-ready" })
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(Folder(), name + ".png"), width, width * 9 / 16, 12, 30);
            var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
            Assert.AreEqual(name == "level-rewards-window-client", await BattlePassRunner.IsRewardsPageAsync(screen, default), name);
            if (name != "tasks-three-ready")
                Assert.IsFalse(await BattlePassRunner.IsTasksPageAsync(screen, default));
        }
    }

    [TestMethod]
    [DataRow("level-rewards-window", true)]
    [DataRow("level-rewards-popup-window", false)]
    public async Task RecognizesOriginalWindowWithTitleBar(string name, bool expected)
    {
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(Folder(), name + ".png"));
        var window = new CaptureTargetWindow { ClientWidth = 2048, ClientHeight = 1152, ClientOffsetX = 1, ClientOffsetY = 31 };
        Assert.AreEqual(expected, await BattlePassRunner.IsRewardsPageAsync(
            new DailyTaskScreen(frame, window, new ImageMatchingService()), default));
    }

    internal static string Folder([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "LocalScreenshots", "2026-11-16", "battle-pass");

    [TestMethod]
    [DataRow(2048)]
    [DataRow(1280)]
    [DataRow(960)]
    public async Task CountsClaimButtonsAndRejectsPopups(int width)
    {
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        foreach (var (name, expected) in new (string, int?)[] { ("tasks-three-ready", 3), ("tasks-four-ready", 4),
            ("tasks-claimed", 0), ("reward-popup", null), ("level-popup", null), ("season-menu", null) })
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(Folder(), name + ".png"), width, width * 9 / 16, 12, 30);
            var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
            Assert.AreEqual(expected.HasValue, await BattlePassRunner.IsTasksPageAsync(screen, default), name);
            if (expected.HasValue)
            {
                Assert.AreEqual(expected.Value, (await BattlePassRunner.FindTaskButtonsAsync(screen, default)).Count, name);
                Assert.AreEqual(expected.Value > 0, await BattlePassRunner.HasClaimAllAsync(screen, default), name);
            }
        }
    }
}
