using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.ImageMatching;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.TextRecognition;
using RocoPilot.Services.TextRecognition.Backends;

namespace RocoPilot.Tests;

[TestClass]
[TestCategory("LocalScreenshots")]
public sealed class LegendaryLocalScreenshotTests
{
    // 契约印记不足弹窗判定依赖 OCR 正文匹配，全部测试使用真实 OCR 服务。
    private static TextRecognitionService CreateOcr()
        => new([new OnnxOcrV5TextRecognitionBackend(new OnnxOcrV5SingleLineTextRecognitionBackend())]);

    private static ManualChallengeScreenRecognizer CreateRecognizer()
        => new(new ImageMatchingService(), CreateOcr(), null!, null!,
            NullLogger<ManualChallengeScreenRecognizer>.Instance);

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
        var recognizer = CreateRecognizer();
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
        var recognizer = CreateRecognizer();
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
    public async Task ResultScreenInsufficientPromptIsRecognizedAndConfirms(int width, int height)
    {
        // 2026-11-08 用户截图：结算页点“再次挑战”后弹出契约印记不足提示，
        // 背景仍可见退出/再次挑战按钮；弹窗须优先于背景结算页识别。
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-result-insufficient.png",
            width, height, 12, 30);
        var recognizer = CreateRecognizer();
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
        Assert.AreEqual(ManualChallengeScene.InsufficientContractMark, screen.Scene);
        Assert.IsNotNull(screen.Button);
        Assert.IsTrue(screen.Button.IsMatch);
        var confirmX = (screen.Button.X + screen.Button.Width / 2d - 12) / width;
        var confirmY = (screen.Button.Y + screen.Button.Height / 2d - 30) / height;
        Assert.IsTrue(confirmX is > .5 and < .68 && confirmY is > .74 and < .92,
            $"确认按钮位置异常：({confirmX:F3}, {confirmY:F3})");
        Assert.IsNotNull(screen.ExitButton);
        Assert.IsTrue(screen.ExitButton.IsMatch);
        // 点击前的复查：同一帧应重新定位到同一个确认按钮。
        Assert.AreEqual(screen.Button, await recognizer.FindClickTargetAsync(frame, screen.Scene,
            screen.Button.TemplatePath, 0, 12, 30, width, height, CancellationToken.None, legendary: true));
        // 回归：契约印记不足弹窗不遮挡背景的"再次挑战"按钮，复查 retry
        // 必须识别到弹窗并返回 null（已切换），否则 Runner 会反复点原按钮直至超时。
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.Result,
            "2048x1152/legendary/result-retry.png", 0, 12, 30, width, height, CancellationToken.None, legendary: true));
    }

    [TestMethod]
    [DataRow(2048, 1152)]
    [DataRow(1280, 720)]
    public async Task PreparationInsufficientPromptIsRecognizedAndStartRecheckYields(int width, int height)
    {
        // 2026-11-08 用户截图：挑战准备页点“开始挑战”后弹出契约印记不足提示，
        // 背景的“开始挑战”按钮仍可见；准备页点击的是 start 模板（非 legendary-challenge），
        // 复查同样必须识别弹窗并让路，否则反复点原按钮直至超时。
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-preparation-insufficient.png",
            width, height, 12, 30);
        var recognizer = CreateRecognizer();
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
        Assert.AreEqual(ManualChallengeScene.InsufficientContractMark, screen.Scene);
        Assert.IsNotNull(screen.Button);
        Assert.IsTrue(screen.Button.IsMatch);
        Assert.IsNotNull(screen.ExitButton);
        Assert.IsTrue(screen.ExitButton.IsMatch);
        // start 复查让路：弹窗在场（取消按钮出现）时视为已切换，返回 null 交给主流程。
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.Preparation,
            "2048x1152/flower-seed/start.png", 0, 12, 30, width, height, CancellationToken.None, legendary: true));
    }

    [TestMethod]
    [DataRow(2048, 1152)]
    [DataRow(1280, 720)]
    public async Task InsufficientPromptIsRecognizedAndDoesNotMatchChallengeButton(int width, int height)
    {
        // legendary-insufficient.png 已更新为 2026-11-08 当前渲染（结算页弹窗）；
        // 同时验证“开始挑战”按钮模板不会误命中弹窗画面（弹窗期间 challenge 复查为空）。
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-insufficient.png",
            width, height, 12, 30);
        var recognizer = CreateRecognizer();
        var screen = await recognizer.ReadAsync(frame, 12, 30, width, height, CancellationToken.None, legendary: true);
        Assert.AreEqual(ManualChallengeScene.InsufficientContractMark, screen.Scene);
        Assert.IsNull(await recognizer.FindClickTargetAsync(frame, ManualChallengeScene.LegendaryConfirmation,
            "2048x1152/legendary/challenge.png", 0, 12, 30, width, height, CancellationToken.None, legendary: true));
    }

    [TestMethod]
    [DataRow(2048, 1152)]
    [DataRow(1280, 720)]
    public async Task PreparationStartIsRecognizedWithoutCursor(int width, int height)
    {
        using var frame = FlowerSeedScreenRecognizerTests.LoadFixture("legendary-local/preparation-start-stalled.png",
            width, height, 12, 30);
        var recognizer = CreateRecognizer();
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
        var recognizer = CreateRecognizer();
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
