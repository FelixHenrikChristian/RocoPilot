using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Input;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class FlowerSeedChallengeRunnerTests
{
    [TestMethod]
    public async Task ScanningProgressUsesNumberedFlowersAndStableOperations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = new FreshListCapture();
        var scrolls = 0;
        var mouse = new Mouse { DuringScroll = () => { if (++scrolls >= 3) capture.Scene = 7; } };
        var runner = CreateRunner(capture, new Images(), new Keyboard(), mouse,
            ocr: new InteractionOcr { Text = string.Empty });
        List<IndependentTaskProgress> updates = [];

        var options = await runner.ScanAsync(CreateState(), updates.Add, cancellation.Token);

        Assert.HasCount(3, options);
        Assert.IsTrue(updates.Any(update => update.Recognition == "正在定位列表顶部"));
        Assert.IsTrue(updates.Any(update => update.Recognition == "当前花种：1. 未识别名称、2. 未识别名称"));
        Assert.IsTrue(updates.Any(update => update.Recognition == "当前花种：2. 未识别名称、3. 未识别名称"));
        Assert.IsTrue(updates.All(update => !update.Recognition.Contains("OCR", StringComparison.Ordinal)));
        Assert.IsTrue(updates.All(update => update.Operation is "回到花种列表顶部" or "向下滚动花种列表" or "扫描完成，共3个花种"));
        Assert.AreEqual("扫描完成，共3个花种", updates[^1].Operation);
    }

    [TestMethod]
    public async Task RepeatedWgcFrameIsRecognizedOnlyOnce()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 2);
        var images = new Images();
        var keyboard = new Keyboard();
        var mouse = new Mouse();
        var runner = CreateRunner(capture, images, keyboard, mouse);

        var state = CreateState();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(state, new FlowerSeedOption(1, "友爱星飞"),
            6, state.Options.AutoBattleSettings, _ => { }, cancellation.Token));

        Assert.AreEqual(1, images.FramesRead);
        Assert.AreEqual(0, keyboard.Keys.Count);
        Assert.AreEqual(0, mouse.Clicks);
    }

    [TestMethod]
    public async Task FrameCapturedDuringInputCannotTriggerTheNextClickAndKeyboardUsesConfiguredHold()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 0);
        var images = new Images();
        var keyboard = new Keyboard { DuringKey = () => capture.Show(1) };
        var mouse = new Mouse();
        var runner = CreateRunner(capture, images, keyboard, mouse);

        var state = CreateState();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(state, new FlowerSeedOption(1, "友爱星飞"),
            6, state.Options.AutoBattleSettings, _ => { }, cancellation.Token));

        CollectionAssert.AreEqual(new[] { "F3" }, keyboard.Keys.Select(key => key.Key).ToArray());
        Assert.AreEqual(175, keyboard.Keys[0].Options.HoldDurationMs);
        Assert.AreEqual(KeyboardInputMethod.Interception, keyboard.Keys[0].Options.Method);
        Assert.AreEqual(1, images.FramesRead);
        Assert.AreEqual(0, mouse.Clicks);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OcrOverlayUsesClientRelativeRegionsAndOnlyPublishesWhenEnabled(bool enabled)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 3, 108, 76);
        var overlay = new Overlay();
        var ocr = new InteractionOcr();
        var runner = CreateRunner(capture, new Images(), new Keyboard(), new Mouse(), overlay, ocr);

        var state = CreateState(enabled, hasBorder: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            state, new FlowerSeedOption(1, "友爱星飞"), 6, state.Options.AutoBattleSettings, _ => { }, cancellation.Token));

        Assert.AreEqual(1, ocr.Calls);
        Assert.AreEqual(1, overlay.Hides);
        if (!enabled)
        {
            Assert.HasCount(0, overlay.Shown);
            Assert.HasCount(0, overlay.Results);
            return;
        }
        Assert.HasCount(2, overlay.Shown);
        Assert.HasCount(1, overlay.Results);
        var config = overlay.Shown[0];
        Assert.AreEqual((96, 54), (config.ResolutionWidth, config.ResolutionHeight));
        Assert.HasCount(1, config.Regions);
        var region = config.Regions[0];
        Assert.AreEqual((49, 20, 7, 2), (region.X, region.Y, region.Width, region.Height));
        Assert.AreEqual(("花种交互选项", "OCR：你好"), overlay.Results[0]);
        Assert.AreEqual(region.Id, overlay.Results[0].Id);
        Assert.HasCount(0, overlay.Shown[1].Regions);
        Assert.AreEqual((96, 54), (overlay.Shown[1].ResolutionWidth, overlay.Shown[1].ResolutionHeight));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ScrollingClearsThePreviousOcrOverlayAndKeepsGlobalNumbersWhenDisplayWasDisabled(bool initiallyEnabled)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = new FreshListCapture();
        var state = CreateState(overlayEnabled: initiallyEnabled);
        var images = new Images();
        var overlay = new Overlay();
        var ocr = new InteractionOcr { Text = string.Empty };
        var scrolls = 0;
        var mouse = new Mouse
        {
            DuringScroll = () =>
            {
                scrolls++;
                if (state.Options.RecognitionOverlayEnabled)
                    Assert.HasCount(0, overlay.Shown[^1].Regions);
                if (scrolls < 3) return;
                capture.Scene = 7;
                state.Options.RecognitionOverlayEnabled = true;
            }
        };
        overlay.DuringShow = config =>
        {
            if (config.Regions.Count > 0 && config.Regions[0].Y == 28) cancellation.Cancel();
        };
        var runner = CreateRunner(capture, images, new Keyboard(), mouse, overlay, ocr);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.ScanAsync(state, _ => { }, cancellation.Token));

        Assert.AreEqual(3, scrolls);
        Assert.AreEqual(images.FramesRead * 2, ocr.Calls);
        var visible = overlay.Shown.Where(config => config.Regions.Count > 0).ToArray();
        Assert.HasCount(initiallyEnabled ? 2 : 1, visible);
        if (initiallyEnabled)
        {
            CollectionAssert.AreEqual(new[] { "花种 1", "花种 2" }, visible[0].Regions.Select(region => region.Id).ToArray());
            CollectionAssert.AreEqual(new[] { 19, 32 }, visible[0].Regions.Select(region => region.Y).ToArray());
        }
        CollectionAssert.AreEqual(new[] { "花种 2", "花种 3" }, visible[^1].Regions.Select(region => region.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 28, 41 }, visible[^1].Regions.Select(region => region.Y).ToArray());
        Assert.IsTrue(visible[^1].Regions.All(region => !region.IsMatched));
        Assert.IsTrue(overlay.Results.All(result => result.Text == "OCR：无文本 → 图鉴：未匹配"));
        Assert.AreEqual(1, overlay.Hides);
    }

    [TestMethod]
    public async Task DisablingOcrOverlayDuringRecognitionStopsPublishing()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new CaptureStub(cancellation, 3, 108, 76);
        var state = CreateState(overlayEnabled: true, hasBorder: true);
        var overlay = new Overlay();
        var ocr = new InteractionOcr { DuringRead = () => state.Options.RecognitionOverlayEnabled = false };
        var runner = CreateRunner(capture, new Images(), new Keyboard(), new Mouse(), overlay, ocr);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            state, new FlowerSeedOption(1, "友爱星飞"), 6, state.Options.AutoBattleSettings, _ => { }, cancellation.Token));

        Assert.AreEqual(1, ocr.Calls);
        Assert.HasCount(0, overlay.Shown);
        Assert.HasCount(0, overlay.Results);
        Assert.AreEqual(1, overlay.Hides);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChallengeUsesDedicatedSkillsAndExclusiveCaptureThenRepeatsAndExits(bool medalConfirmation)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var capture = new ChallengeCapture(medalConfirmation);
        var state = CreateState();
        state.Options.AutoBattleSettings.IsEnabled = false;
        state.Options.AutoBattleSettings.TurnSequence = "R, {skill}";
        state.Options.AutoBattleSettings.FlowerSeedReleaseSequence =
            [AutoBattleReleaseStep.CreateSkill("4"), AutoBattleReleaseStep.CreateSkill("2")];
        var keyboard = new Keyboard { DuringSequence = capture.OnKey };
        var mouse = new Mouse { DuringClick = capture.OnClick };
        var overlay = new Overlay();
        var runner = CreateRunner(capture, new Images(), keyboard, mouse, overlay,
            new InteractionOcr { ReadText = frame => frame.Pixels[0] == 3 ? "挑战" : string.Empty });
        List<IndependentTaskProgress> updates = [];

        await runner.RunAsync(state, new FlowerSeedOption(1, "友爱星飞"), 2,
            state.Options.AutoBattleSettings, updates.Add, cancellation.Token);

        CollectionAssert.AreEqual(new[] { "F", "4", "1, Space", "4", "1, Space" },
            keyboard.Keys.Select(key => key.Key).ToArray());
        Assert.IsFalse(state.Options.AutoBattleSettings.IsEnabled);
        Assert.IsTrue(keyboard.Keys.All(key => key.Options.HoldDurationMs == 175
            && key.Options.Method == KeyboardInputMethod.Interception));
        Assert.IsTrue(keyboard.Keys.Where(key => key.Key == "1, Space")
            .All(key => key.Options.IntervalMs == state.Options.AutoBattleSettings.CaptureKeyboardIntervalMs));
        Assert.AreEqual(2, capture.BattlesStarted);
        Assert.AreEqual(1, capture.Retries);
        Assert.AreEqual(1, capture.Exits);
        Assert.AreEqual(4, capture.AnimationFrames);
        Assert.AreEqual(4, capture.CaptureTransitionFrames);
        Assert.AreEqual(medalConfirmation ? 1 : 0, capture.MedalStarts);
        Assert.IsTrue(updates.Any(update => update.Recognition.Contains("正在确认当前界面", StringComparison.Ordinal)));
        Assert.IsTrue(updates.Any(update => update.Operation == "技能 4"));
        Assert.IsTrue(updates.Any(update => update.Operation == "捕捉"));
        Assert.IsTrue(updates.All(update => update.CreatureName == "友爱星飞"));
        Assert.IsFalse(updates.Any(update => update.Operation.Contains('（') || update.Operation.Contains("已发送", StringComparison.Ordinal)));
        Assert.AreEqual("挑战完成，成功2次，已返回大世界", updates[^1].Operation);
        Assert.AreEqual(1, overlay.Hides);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LosingForegroundPausesInputUntilTheUserReturnsToTheGame(bool duringRecognition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var capture = new ChallengeCapture();
        var state = CreateState();
        state.Options.AutoBattleSettings.KeyboardInputMethod = KeyboardInputMethod.PostMessage;
        state.Options.AutoBattleSettings.FlowerSeedReleaseSequence = [AutoBattleReleaseStep.CreateSkill("4")];
        var keyboard = new Keyboard();
        var mouse = new Mouse { DuringClick = capture.OnClick };
        var foregroundLost = false;
        keyboard.DuringSequence = sequence =>
        {
            capture.OnKey(sequence);
            if (!duringRecognition && sequence == "4") keyboard.Foreground = false;
        };
        var runner = CreateRunner(capture, new Images(), keyboard, mouse,
            ocr: new InteractionOcr
            {
                ReadText = frame =>
                {
                    if (frame.Pixels[0] != 3) return string.Empty;
                    if (duringRecognition && !foregroundLost)
                    {
                        foregroundLost = true;
                        keyboard.Foreground = false;
                    }
                    return "挑战";
                }
            });
        Task? userReturn = null;
        await runner.RunAsync(state, new FlowerSeedOption(1, "友爱星飞"), 1, state.Options.AutoBattleSettings, update =>
        {
            if (update.Operation != "已暂停，等待切回游戏" || userReturn is not null) return;
            Assert.AreEqual(duringRecognition ? 0 : 2, keyboard.Keys.Count);
            userReturn = Task.Run(async () =>
            {
                var frames = capture.Frames;
                var inputs = keyboard.Keys.Count;
                var clicks = mouse.Clicks;
                await Task.Delay(650);
                Assert.AreEqual(frames, capture.Frames);
                Assert.AreEqual(inputs, keyboard.Keys.Count);
                Assert.AreEqual(clicks, mouse.Clicks);
                keyboard.Foreground = true;
            });
        }, cancellation.Token);

        Assert.IsNotNull(userReturn);
        await userReturn;
        CollectionAssert.AreEqual(new[] { "F", "4", "1, Space" }, keyboard.Keys.Select(key => key.Key).ToArray());
        Assert.AreEqual(1, capture.Exits);
    }

    [TestMethod]
    public async Task IntermittentUnrecognizedSkillFramesDoNotRestartTheActionDelay()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var capture = new ChallengeCapture();
        var images = new Images { IntermittentSkills = true };
        var state = CreateState();
        state.Options.AutoBattleSettings.FlowerSeedReleaseSequence = [AutoBattleReleaseStep.CreateSkill("4")];
        var keyboard = new Keyboard { DuringSequence = capture.OnKey };
        var runner = CreateRunner(capture, images, keyboard, new Mouse { DuringClick = capture.OnClick },
            ocr: new InteractionOcr { ReadText = frame => frame.Pixels[0] == 3 ? "挑战" : string.Empty });

        await runner.RunAsync(state, new FlowerSeedOption(1, "友爱星飞"), 1,
            state.Options.AutoBattleSettings, _ => { }, cancellation.Token);

        CollectionAssert.AreEqual(new[] { "F", "4", "1, Space" }, keyboard.Keys.Select(key => key.Key).ToArray());
        Assert.IsTrue(images.SkillFrames >= 4);
        Assert.AreEqual(1, capture.Exits);
    }

    [TestMethod]
    public async Task FailedChallengeRetriesWithFAtTheSameFlowerAndRestartsTheFirstSkill()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var capture = new ChallengeCapture(failFirstBattle: true);
        var state = CreateState();
        state.Options.AutoBattleSettings.FlowerSeedReleaseSequence =
            [AutoBattleReleaseStep.CreateSkill("4"), AutoBattleReleaseStep.CreateSkill("2")];
        var keyboard = new Keyboard { DuringSequence = capture.OnKey };
        var mouse = new Mouse { DuringClick = capture.OnClick };
        var runner = CreateRunner(capture, new Images(), keyboard, mouse,
            ocr: new InteractionOcr { ReadText = frame => frame.Pixels[0] == 3 ? "挑战" : string.Empty });
        List<IndependentTaskProgress> updates = [];

        await runner.RunAsync(state, new FlowerSeedOption(1, "友爱星飞"), 1,
            state.Options.AutoBattleSettings, updates.Add, cancellation.Token);

        CollectionAssert.AreEqual(new[] { "F", "4", "F", "4", "1, Space" },
            keyboard.Keys.Select(key => key.Key).ToArray());
        Assert.AreEqual(1, capture.Teleports);
        Assert.AreEqual(2, capture.BattlesStarted);
        Assert.AreEqual(0, capture.Retries);
        Assert.AreEqual(1, capture.Exits);
        Assert.AreEqual(2, capture.CaptureTransitionFrames);
        Assert.AreEqual("挑战完成，成功1次，已返回大世界", updates[^1].Operation);
    }

    private static FlowerSeedChallengeRunner CreateRunner(IScreenCaptureService capture, Images images, Keyboard keyboard, Mouse mouse,
        Overlay? overlay = null, ITextRecognitionService? ocr = null)
        => new(capture, keyboard, mouse,
            new FlowerSeedScreenRecognizer(images, ocr ?? new UnusedOcr(), null!, null!, NullLogger<FlowerSeedScreenRecognizer>.Instance),
            new AutoBattleInputExecutor(keyboard, NullLogger<AutoBattleInputExecutor>.Instance),
            overlay ?? new Overlay(),
            NullLogger<FlowerSeedChallengeRunner>.Instance);

    private static RuntimeTaskState CreateState(bool overlayEnabled = false, bool hasBorder = false) => new(new CaptureTargetWindow
    {
        Hwnd = (nint)1, ClientWidth = 96, ClientHeight = 54,
        Width = hasBorder ? 108 : 96, Height = hasBorder ? 76 : 54,
        WindowClientOffsetX = hasBorder ? 6 : 0, WindowClientOffsetY = hasBorder ? 22 : 0
    }, new RecognitionRegionConfig(), new RuntimeTaskStartOptions
    {
        CaptureMethod = CaptureMethod.WindowsGraphicsCapture,
        RecognitionOverlayEnabled = overlayEnabled,
        AutoBattleSettings = new() { KeyboardInputMethod = KeyboardInputMethod.Interception, KeyboardHoldDurationMs = 175 }
    }, DateTimeOffset.UtcNow);

    private sealed class CaptureStub(CancellationTokenSource cancellation, byte initialScene, int width = 96, int height = 54)
        : IScreenCaptureService, IDisposable
    {
        private CapturedFrame? _cached;
        private int _calls;
        public byte? NextScene;
        public CapturedFrame? Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
        {
            if (_cached is null) Show(initialScene);
            if (NextScene is { } scene)
            {
                Show(scene);
                NextScene = null;
            }
            if (++_calls >= 4) cancellation.Cancel();
            return _cached!.AddReference();
        }
        public void Show(byte scene)
        {
            _cached?.Dispose();
            var pixels = new byte[width * height * 4];
            if (scene is 3 or 4 or 5) Array.Fill(pixels, (byte)245);
            pixels[0] = scene;
            _cached = new CapturedFrame(width, height, pixels);
        }
        public void Release(CaptureTargetWindow targetWindow, CaptureMethod method) { }
        public void Dispose() => _cached?.Dispose();
    }

    private sealed class FreshListCapture : IScreenCaptureService
    {
        public byte Scene = 6;
        public CapturedFrame Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
        {
            var pixels = new byte[96 * 54 * 4];
            Array.Fill(pixels, (byte)245);
            pixels[0] = Scene;
            return new(96, 54, pixels);
        }
        public void Release(CaptureTargetWindow targetWindow, CaptureMethod method) { }
    }

    // 每次截图都是新帧；捕捉后的过渡画面会短暂命中大世界与交互 HUD。
    private sealed class ChallengeCapture(bool medalConfirmation = false, bool failFirstBattle = false) : IScreenCaptureService
    {
        private byte _scene = 6;
        private int _animationFramesLeft;
        public int BattlesStarted;
        public int Retries;
        public int Exits;
        public int Teleports;
        public int MedalStarts;
        public int AnimationFrames;
        public int CaptureTransitionFrames;
        public int Frames;

        public CapturedFrame Capture(CaptureTargetWindow targetWindow, CaptureMethod method)
        {
            Frames++;
            var scene = _scene;
            if (scene == 2)
            {
                AnimationFrames++;
                if (--_animationFramesLeft == 0) _scene = 15;
            }
            else if (scene is 18 or 19)
            {
                CaptureTransitionFrames++;
                _scene = scene == 18 ? (byte)19 : (byte)16;
                scene = scene == 18 ? (byte)0 : (byte)3;
            }
            var pixels = new byte[96 * 54 * 4];
            Array.Fill(pixels, (byte)245);
            pixels[0] = scene;
            return new(96, 54, pixels);
        }

        public void OnKey(string sequence)
        {
            switch ((_scene, sequence))
            {
                case (3, "F"):
                    _scene = 9;
                    break;
                case (12, "4"):
                    if (failFirstBattle && BattlesStarted == 1) _scene = 3;
                    else
                    {
                        _scene = 2;
                        _animationFramesLeft = 2;
                    }
                    break;
                case (15, "1, Space"):
                    _scene = 18;
                    break;
                default:
                    Assert.Fail($"场景 {_scene} 收到未预期按键：{sequence}");
                    break;
            }
        }

        public void OnClick(int x, int y)
        {
            switch (_scene)
            {
                case 6:
                    _scene = 8;
                    break;
                case 8:
                    Teleports++;
                    _scene = 3;
                    break;
                case 9:
                    _scene = 10;
                    break;
                case 10 when medalConfirmation && BattlesStarted == 0:
                    _scene = 11;
                    break;
                case 10:
                    StartBattle();
                    break;
                case 11:
                    MedalStarts++;
                    StartBattle();
                    break;
                case 16:
                    _scene = 17;
                    break;
                case 17 when x == 60:
                    Retries++;
                    StartBattle();
                    break;
                case 17 when x == 41:
                    Exits++;
                    _scene = 0;
                    break;
                default:
                    Assert.Fail($"场景 {_scene} 收到未预期点击：({x}, {y})");
                    break;
            }
        }

        private void StartBattle()
        {
            BattlesStarted++;
            _scene = 12;
        }

        public void Release(CaptureTargetWindow targetWindow, CaptureMethod method) { }
    }

    private sealed class Images : IImageMatchingService
    {
        public int FramesRead;
        public bool IntermittentSkills;
        public int SkillFrames;
        public ImageMatchAlgorithm DefaultAlgorithm => ImageMatchAlgorithm.OpenCvSqDiffNormalized;
        public string TemplateDirectory => "";
        public IReadOnlyList<string> ListTemplatePaths() => [];
        public Task<ImageMatchResult> MatchAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            ImageMatchOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (templatePath.EndsWith("/flower-tab.png", StringComparison.Ordinal))
            {
                FramesRead++;
                if (frame.Pixels[0] == 12) SkillFrames++;
            }
            if (IntermittentSkills && frame.Pixels[0] == 12 && SkillFrames % 2 == 0)
                return Task.FromResult(ImageMatchResult.NoMatch(0, templatePath));
            if (frame.Pixels[0] is 4 or 5 or 6 or 7 && templatePath.EndsWith("/flower-tab.png", StringComparison.Ordinal))
                return Task.FromResult(new ImageMatchResult(true, 1, 24, 3, 6, 2, templatePath));
            if (frame.Pixels[0] == 3 && templatePath.EndsWith("/interaction-f.png", StringComparison.Ordinal))
                return Task.FromResult(new ImageMatchResult(true, 1, 51, 42, 2, 2, templatePath));
            var scenarioMatch = (frame.Pixels[0], Path.GetFileNameWithoutExtension(templatePath)) switch
            {
                (8, "map-header" or "map-teleport") => true,
                (9, "confirmation-header" or "confirmation-challenge" or "confirmation-cancel") => true,
                (10, "preparation" or "start") => true,
                (11, "medal-confirmation" or "start") => true,
                (12, "battle-button-skill") => true,
                (15, "capture-exclusive" or "capture-button") => true,
                (16, "rewards") => true,
                (17, "result-retry" or "result-exit") => true,
                _ => false
            };
            if (scenarioMatch)
                return Task.FromResult(new ImageMatchResult(true, 1,
                    templatePath.EndsWith("/result-exit.png", StringComparison.Ordinal) ? 36 : 55,
                    48, 10, 4, templatePath));
            var matched = frame.Pixels[0] is 0 or 3 && templatePath.EndsWith("/magic-point.png", StringComparison.Ordinal)
                || frame.Pixels[0] == 1 && templatePath.EndsWith("/manual-header.png", StringComparison.Ordinal);
            return Task.FromResult(matched ? new ImageMatchResult(true, 1, 10, 1, 2, 2, templatePath) : ImageMatchResult.NoMatch(0, templatePath));
        }
        public Task<ImageMatchCollectionResult> FindMatchesAsync(CapturedFrame frame, RecognitionRegion region,
            string templatePath, int maximumMatches, ImageMatchOptions? options = null,
            double maximumOverlapRatio = .5, CancellationToken cancellationToken = default)
            => Task.FromResult(frame.Pixels[0] switch
            {
                4 or 5 => new ImageMatchCollectionResult([new(true, 1, 80, frame.Pixels[0] == 4 ? 20 : 26, 10, 4, templatePath)], 1, templatePath),
                6 or 7 => new ImageMatchCollectionResult(Enumerable.Range(0, 2).Select(index => new ImageMatchResult(
                    true, 1, 80, (frame.Pixels[0] == 6 ? 20 : 29) + index * 13, 10, 4, templatePath)).ToArray(), 1, templatePath),
                _ => ImageMatchCollectionResult.NoMatch(0, templatePath)
            });
    }

    private sealed class Keyboard : IKeyboardInputService
    {
        public Action? DuringKey;
        public Action<string>? DuringSequence;
        public List<(string Key, KeyboardInputOptions Options)> Keys { get; } = [];
        public volatile bool Foreground = true;
        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => Foreground;
        public bool RequiresForeground(KeyboardInputMethod method) => method != KeyboardInputMethod.PostMessage;
        public void EnsureReady(KeyboardInputMethod method) { }
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keyStrokes, out string error)
        {
            keyStrokes = sequence.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(key => new KeyStroke([], new KeyDefinition(key, 0))).ToArray();
            error = "";
            return keyStrokes.Count > 0;
        }
        public async Task SendSequenceAsync(nint hwnd, string sequence, KeyboardInputOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Keys.Add((sequence, options!));
            DuringKey?.Invoke();
            DuringSequence?.Invoke(sequence);
            await Task.Delay(10, cancellationToken);
        }
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keyStrokes, KeyboardInputOptions? options = null,
            CancellationToken cancellationToken = default)
            => SendSequenceAsync(hwnd, string.Join(", ", keyStrokes.Select(key => key.DisplayText)), options, cancellationToken);
    }

    private sealed class Mouse : IMouseInputService
    {
        public int Clicks;
        public Action? DuringScroll;
        public Action<int, int>? DuringClick;
        public Task ClickAsync(nint hwnd, int x, int y, KeyboardInputMethod method = KeyboardInputMethod.SendInput,
            CancellationToken cancellationToken = default) { Clicks++; DuringClick?.Invoke(x, y); return Task.CompletedTask; }
        public Task ScrollAsync(nint hwnd, int x, int y, int delta, KeyboardInputMethod method = KeyboardInputMethod.SendInput,
            CancellationToken cancellationToken = default) { DuringScroll?.Invoke(); return Task.CompletedTask; }
        public Task ScrollAtCurrentPositionAsync(nint hwnd, int delta, KeyboardInputMethod method = KeyboardInputMethod.SendInput,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Overlay : IRecognitionOverlayService
    {
        public List<RecognitionRegionConfig> Shown { get; } = [];
        public List<(string Id, string Text)> Results { get; } = [];
        public int Hides;
        public Action<RecognitionRegionConfig>? DuringShow;
        public void Show(RuntimeTaskState state, RecognitionRegionConfig? regionConfig = null)
        {
            Shown.Add(regionConfig!);
            DuringShow?.Invoke(regionConfig!);
        }
        public void ShowOcrResult(string regionId, string text) => Results.Add((regionId, text));
        public void ShowImageMatchResult(string regionId, double score) => throw new NotSupportedException();
        public void Hide() => Hides++;
    }

    private sealed class InteractionOcr : ITextRecognitionService
    {
        public int Calls;
        public string Text = "你好";
        public Action? DuringRead;
        public Func<CapturedFrame, string>? ReadText;
        public IReadOnlyList<TextRecognitionMethodOption> GetMethods() => [];
        public TextRecognitionMethodOption? GetDefaultMethod() => null;
        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TextRecognitionResult> RecognizeAsync(CapturedFrame frame, RecognitionRegion region,
            TextRecognitionMethod method, CancellationToken cancellationToken = default)
        {
            Calls++;
            DuringRead?.Invoke();
            return Task.FromResult(new TextRecognitionResult(method, "ONNX", null, [ReadText?.Invoke(frame) ?? Text], 1));
        }
    }

    private sealed class UnusedOcr : ITextRecognitionService
    {
        public IReadOnlyList<TextRecognitionMethodOption> GetMethods() => [new(TextRecognitionMethod.OnnxOcrV5, "ONNX", "", true)];
        public TextRecognitionMethodOption? GetDefaultMethod() => GetMethods()[0];
        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TextRecognitionResult> RecognizeAsync(CapturedFrame frame, RecognitionRegion region, TextRecognitionMethod method,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
