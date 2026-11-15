using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Capture;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

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
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        var window = new CaptureTargetWindow { ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        foreach (var name in new[] { "level-rewards-window-client", "level-rewards-popup-window-client", "tasks-three-ready" })
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(Folder(), name + ".png"), width, width * 9 / 16, 12, 30);
            var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
            Assert.AreEqual(name == "level-rewards-window-client", await BattlePassRunner.ReadRewardsPageAsync(frame, screen, ocr, default), name);
            if (name != "tasks-three-ready")
                Assert.IsNull(await BattlePassRunner.ReadCompletedAsync(frame, screen, ocr, default));
        }
    }

    [TestMethod]
    [DataRow("level-rewards-window", true)]
    [DataRow("level-rewards-popup-window", false)]
    public async Task RecognizesOriginalWindowWithTitleBar(string name, bool expected)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(Folder(), name + ".png"));
        var window = new CaptureTargetWindow { ClientWidth = 2048, ClientHeight = 1152, ClientOffsetX = 1, ClientOffsetY = 31 };
        Assert.AreEqual(expected, await BattlePassRunner.ReadRewardsPageAsync(frame,
            new DailyTaskScreen(frame, window, new ImageMatchingService()), ocr, default));
    }

    internal static string Folder([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "LocalScreenshots", "2026-11-16", "battle-pass");

    [TestMethod]
    [DataRow(2048)]
    [DataRow(1280)]
    [DataRow(960)]
    public async Task ReadsTasksAndRejectsPopups(int width)
    {
        using var backend = new OnnxOcrV5SingleLineTextRecognitionBackend();
        var ocr = new TextRecognitionService([new OnnxOcrV5TextRecognitionBackend(backend)]);
        var window = new CaptureTargetWindow { Hwnd = 1, ClientWidth = width, ClientHeight = width * 9 / 16,
            ClientOffsetX = 12, ClientOffsetY = 30 };
        foreach (var (name, expected) in new (string, int?)[] { ("tasks-three-ready", 3), ("tasks-four-ready", 4),
            ("tasks-claimed", 4), ("reward-popup", null), ("level-popup", null), ("season-menu", null) })
        {
            using var frame = FlowerSeedScreenRecognizerTests.LoadFixture(Path.Combine(Folder(), name + ".png"), width, width * 9 / 16, 12, 30);
            var screen = new DailyTaskScreen(frame, window, new ImageMatchingService());
            Assert.AreEqual(expected, await BattlePassRunner.ReadCompletedAsync(frame, screen, ocr, default), $"{width}: {name}");
            if (name.StartsWith("tasks-"))
            {
                Assert.AreEqual("日常任务", await BattlePassRunner.ReadAsync(frame, screen, ocr, 195, 57, 340, 96, default));
                var claim = await BattlePassRunner.ReadAsync(frame, screen, ocr, 1460, 1063, 1608, 1106, default);
                Assert.AreEqual(name != "tasks-claimed", claim == "全部领取", $"{name}: {claim}");
                Assert.AreEqual(name != "tasks-claimed", await BattlePassRunner.HasClaimableAsync(frame, screen, ocr, default));
            }
        }
    }
}
