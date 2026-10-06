using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Core.Battle;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Recognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Hotkeys;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Input;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RuntimeTaskStartupTests
{
    [TestMethod]
    [DataRow("Interception 驱动未安装或尚未重启。")]
    [DataRow("Interception 初始化失败。")]
    [DataRow("Interception 未找到可用键盘设备。")]
    public async Task UnavailableInputFailsBeforeForegroundCaptureAndSession(string failure)
    {
        var fixture = new StartupFixture();
        fixture.Keyboard.Failure = failure;

        var result = await fixture.Runtime.StartAsync(Options());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, failure);
        Assert.IsFalse(fixture.Runtime.IsRunning);
        Assert.IsNull(fixture.Runtime.CurrentState);
        Assert.AreEqual(0, fixture.Window.ForegroundCalls);
        Assert.AreEqual(0, fixture.Capture.Calls);
        Assert.AreEqual(0, fixture.Overlay.Shows);
        Assert.AreEqual(0, fixture.Keyboard.Sends);
    }

    [TestMethod]
    [DataRow(KeyboardInputMethod.Interception)]
    [DataRow(KeyboardInputMethod.SendInput)]
    [DataRow(KeyboardInputMethod.PostMessage)]
    public async Task ReadyInputStartsWithoutSendingKeys(KeyboardInputMethod method)
    {
        var fixture = new StartupFixture();
        try
        {
            var result = await fixture.Runtime.StartAsync(Options(method: method));
            Assert.IsTrue(result.Success, result.Message);
            Assert.IsTrue(fixture.Runtime.IsRunning);
            CollectionAssert.AreEqual(new[] { method }, fixture.Keyboard.CheckedMethods);
            Assert.IsTrue(fixture.Capture.Calls > 0);
            Assert.AreEqual(2, fixture.Overlay.Shows);
            Assert.AreEqual(0, fixture.Keyboard.Sends);
        }
        finally { await fixture.Runtime.StopAsync(); }
        Assert.IsFalse(fixture.Runtime.IsRunning);
        Assert.AreEqual(1, fixture.Capture.Releases);
    }

    [TestMethod]
    public async Task DisabledAutoBattleDoesNotRequireInputDriver()
    {
        var fixture = new StartupFixture();
        fixture.Keyboard.Failure = "驱动不可用";
        try
        {
            var result = await fixture.Runtime.StartAsync(Options(enabled: false));
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(0, fixture.Keyboard.CheckedMethods.Count);
            Assert.AreEqual(0, fixture.Window.ForegroundCalls);
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task MissingGameWindowSkipsInputPreparation()
    {
        var fixture = new StartupFixture();
        fixture.Window.Available = false;
        var result = await fixture.Runtime.StartAsync(Options());
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "未找到目标游戏窗口");
        Assert.AreEqual(0, fixture.Keyboard.CheckedMethods.Count);
    }

    [TestMethod]
    public async Task InputRecoveryAllowsRetryAfterFailedStart()
    {
        var fixture = new StartupFixture();
        fixture.Keyboard.Failure = "未找到可用键盘设备";
        Assert.IsFalse((await fixture.Runtime.StartAsync(Options())).Success);
        fixture.Keyboard.Failure = null;
        try
        {
            var result = await fixture.Runtime.StartAsync(Options());
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(2, fixture.Keyboard.CheckedMethods.Count);
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task CancelDuringInputPreparationDoesNotStartSession()
    {
        var fixture = new StartupFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Keyboard.OnCheck = cancellation.Cancel;
        var result = await fixture.Runtime.StartAsync(Options(), cancellation.Token);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "已取消");
        Assert.IsFalse(fixture.Runtime.IsRunning);
        Assert.AreEqual(0, fixture.Capture.Calls);
        Assert.AreEqual(0, fixture.Overlay.Shows);
    }

    [TestMethod]
    public void BuiltInInputMethodsNeedNoInterceptionInitialization()
    {
        var keyboard = new KeyboardInputService();
        keyboard.EnsureReady(KeyboardInputMethod.PostMessage);
        keyboard.EnsureReady(KeyboardInputMethod.SendInput);
        Assert.ThrowsExactly<InvalidOperationException>(() => keyboard.EnsureReady((KeyboardInputMethod)999));
    }

    [TestMethod]
    public async Task DisabledAutoBattleStillRecognizesWorldAndBattleScenes()
    {
        using var fixture = new StartupFixture(sceneRecognition: true);
        try
        {
            Assert.IsTrue((await fixture.Runtime.StartAsync(Options(enabled: false))).Success);
            fixture.Matching.MagicPoints = 4;
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(InfoOverlayScene.World, fixture.Overlay.Snapshot!.Scene);
            Assert.AreEqual(4, fixture.Overlay.Snapshot.MagicPointCount);

            fixture.Matching.MagicPoints = 0;
            fixture.Matching.MatchedTemplate = "battle-chat.png";
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(InfoOverlayScene.Battle, fixture.Overlay.Snapshot.Scene);
            Assert.AreEqual("战斗中", fixture.Overlay.Snapshot.MainStatusText);
            Assert.AreEqual(0, fixture.Keyboard.Sends);
            Assert.AreEqual(0, fixture.Keyboard.CheckedMethods.Count);
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task SkillSelectionThenUnmatchedMenuStopsBattleAndClearsItsTitle()
    {
        using var fixture = new StartupFixture(sceneRecognition: true);
        try
        {
            Assert.IsTrue((await fixture.Runtime.StartAsync(Options())).Success);
            fixture.Matching.MatchedTemplate = "battle-button-skill.png";
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(InfoOverlayScene.Battle, fixture.Overlay.Snapshot!.Scene);
            Assert.AreEqual("自动战斗中", fixture.Overlay.Snapshot.MainStatusText);
            Assert.AreEqual("战斗中 - 技能选择", fixture.Overlay.Snapshot.StatusText);
            Assert.IsNotNull(fixture.Battle.CurrentTurn);

            fixture.Matching.MatchedTemplate = null;
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(InfoOverlayScene.Unknown, fixture.Overlay.Snapshot.Scene);
            Assert.AreEqual("状态待识别", fixture.Overlay.Snapshot.MainStatusText);
            Assert.IsNull(fixture.Battle.CurrentTurn);

            fixture.SetField("_unrecognizedStateDetectedAt", DateTimeOffset.Now.AddSeconds(-3));
            await fixture.ProcessFrameAsync();
            Assert.AreEqual("状态未识别", fixture.Overlay.Snapshot.MainStatusText);
            Assert.AreEqual(0, fixture.Keyboard.Sends);
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task UnknownAnimationKeepsBattleIdentityUntilConfirmedWorld()
    {
        using var fixture = new StartupFixture(sceneRecognition: true);
        try
        {
            Assert.IsTrue((await fixture.Runtime.StartAsync(Options(enabled: false))).Success);
            fixture.Matching.MatchedTemplate = "battle-chat.png";
            await fixture.ProcessFrameAsync();
            var battleId = fixture.Battle.BattleId;

            fixture.Matching.MatchedTemplate = null;
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(battleId, fixture.Battle.BattleId);
            Assert.AreEqual(InfoOverlayScene.Unknown, fixture.Overlay.Snapshot!.Scene);

            fixture.Matching.MagicPoints = 6;
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(InfoOverlayScene.World, fixture.Overlay.Snapshot.Scene);
            Assert.AreEqual("大世界", fixture.Overlay.Snapshot.MainStatusText);
            Assert.IsTrue(fixture.Battle.BattleId > battleId);
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task UnknownSceneHidesShinyProtectionWithoutClearingBattleProtection()
    {
        using var fixture = new StartupFixture(sceneRecognition: true);
        try
        {
            Assert.IsTrue((await fixture.Runtime.StartAsync(Options())).Success);
            fixture.Matching.MatchedTemplate = "battle-chat.png";
            Assert.IsTrue(fixture.Battle.ObserveShiny(fixture.Battle.BattleId));
            await fixture.ProcessFrameAsync();
            Assert.AreEqual("异色保护", fixture.Overlay.Snapshot!.MainStatusText);
            Assert.IsTrue(fixture.Overlay.Snapshot.IsShinyProtectionActive);

            fixture.Matching.MatchedTemplate = null;
            await fixture.ProcessFrameAsync();
            Assert.AreEqual(InfoOverlayScene.Unknown, fixture.Overlay.Snapshot.Scene);
            Assert.AreEqual("状态待识别", fixture.Overlay.Snapshot.MainStatusText);
            Assert.IsFalse(fixture.Overlay.Snapshot.IsShinyProtectionActive);
            Assert.IsTrue(fixture.Battle.IsSuspendedForShiny,
                "未识别场景不能解除本场战斗已经确认的异色保护。");
            Assert.AreEqual(0, fixture.Keyboard.Sends);
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task CaptureLoopPublishesOnlyConfirmedBattleFramesToOcr()
    {
        using var fixture = new StartupFixture(sceneRecognition: true);
        try
        {
            Assert.IsTrue((await fixture.Runtime.StartAsync(Options(enabled: false))).Success);
            fixture.Matching.MatchedTemplate = "battle-chat.png";
            Assert.IsTrue(await fixture.WaitForCapturedSceneAsync(InfoOverlayScene.Battle),
                "已确认的战斗截图应进入后台战斗 OCR 的帧缓存。");

            fixture.Matching.MatchedTemplate = null;
            Assert.IsFalse(await fixture.WaitForCapturedSceneAsync(InfoOverlayScene.Unknown),
                "未匹配的手册画面必须移除上一帧战斗截图，不能交给战斗 OCR。");

            fixture.Matching.MagicPoints = 4;
            Assert.IsFalse(await fixture.WaitForCapturedSceneAsync(InfoOverlayScene.World),
                "大世界截图不应进入后台战斗 OCR 的帧缓存。");
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    private static RuntimeTaskStartOptions Options(bool enabled = true,
        KeyboardInputMethod method = KeyboardInputMethod.Interception) => new()
    {
        AutoBattleSettings = new() { IsEnabled = enabled, KeyboardInputMethod = method }
    };

    private sealed class StartupFixture : IDisposable
    {
        public WindowStub Window { get; } = new();
        public KeyboardStub Keyboard { get; } = new();
        public CaptureStub Capture { get; } = new();
        public OverlayStub Overlay { get; } = new();
        public MatchingStub Matching { get; }
        public RuntimeTaskService Runtime { get; }
        public AutoBattleController Battle => (AutoBattleController)GetField("_battle")!;

        public StartupFixture(bool sceneRecognition = false)
        {
            var settings = new ControlledSettingsStore();
            Matching = new(sceneRecognition);
            var debug = new RuntimeDebugLogger(NullLogger<RuntimeDebugLogger>.Instance);
            // 仅配置场景识别区域，测试不执行文字识别。
            var recognizer = new RuntimeFrameRecognizer(Matching, null!, Overlay, debug);
            var battleScreen = new BattleScreenRecognizer(recognizer);
            Runtime = new(Window, Keyboard, Capture, new RegionConfigStub(), Matching, recognizer,
                new GameSceneRecognizer(recognizer, Matching, Overlay, debug, battleScreen), battleScreen,
                new AutoBattleInputExecutor(Keyboard, NullLogger<AutoBattleInputExecutor>.Instance), debug,
                new StatisticsSeasonConfigStub(), new StatisticsSpiritCatalogStub(),
                new StatisticsService(settings, NullLogger<StatisticsService>.Instance), settings,
                new HotkeyStub(), Overlay, Overlay, NullLogger<RuntimeTaskService>.Instance);
            Capture.BeforeCapture = () =>
            {
                Assert.AreEqual(0, Overlay.Shows, "启动前截图必须发生在创建遮罩之前。");
            };
        }

        public async Task ProcessFrameAsync()
        {
            using var frame = CapturedFrame.RentBgra32(16, 9);
            var process = typeof(RuntimeTaskService).GetMethod("ProcessFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)process.Invoke(Runtime, [Runtime.CurrentState!, frame, CancellationToken.None])!;
        }

        public async Task<bool> WaitForCapturedSceneAsync(InfoOverlayScene scene)
        {
            var processed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Capture.BeforeWorkerCapture = () =>
            {
                if (Overlay.Snapshot?.Scene != scene) return;
                var session = (RuntimeSession)GetField("_session")!;
                using var frame = session.RentFrame(Battle.BattleId);
                processed.TrySetResult(frame is not null);
            };
            Capture.ContinuousFrames = true;
            try { return await processed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Capture.BeforeWorkerCapture = null; }
        }

        public void SetField(string name, object? value) =>
            typeof(RuntimeTaskService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Runtime, value);
        private object? GetField(string name) =>
            typeof(RuntimeTaskService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Runtime);
        public void Dispose() => Matching.Dispose();
    }

    private sealed class WindowStub : IGameWindowService
    {
        public bool Available = true;
        public int ForegroundCalls;
        public string TargetProcessName => "test-game";
        public CaptureTargetWindow? FindGameWindow() => Available ? new() { Hwnd = 1 } : null;
        public bool TryBringGameWindowToForeground(CaptureTargetWindow window) { ForegroundCalls++; return true; }
    }

    private sealed class KeyboardStub : IKeyboardInputService
    {
        public string? Failure;
        public Action? OnCheck;
        public List<KeyboardInputMethod> CheckedMethods = [];
        public int Sends;
        public void EnsureReady(KeyboardInputMethod method)
        {
            CheckedMethods.Add(method);
            OnCheck?.Invoke();
            if (Failure is not null) throw new InvalidOperationException(Failure);
        }
        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => true;
        public bool RequiresForeground(KeyboardInputMethod method) => method != KeyboardInputMethod.PostMessage;
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> keyStrokes, out string error)
        { keyStrokes = []; error = ""; return true; }
        public Task SendSequenceAsync(nint hwnd, string sequence, KeyboardInputOptions? options = null, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> keyStrokes, KeyboardInputOptions? options = null, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }
    }

    private sealed class CaptureStub : IScreenCaptureService
    {
        public int Calls;
        public int Releases;
        public Action? BeforeCapture;
        public volatile Action? BeforeWorkerCapture;
        public volatile bool ContinuousFrames;
        public CapturedFrame? Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                BeforeCapture?.Invoke();
                return CapturedFrame.RentBgra32(16, 9);
            }
            BeforeWorkerCapture?.Invoke();
            return ContinuousFrames ? CapturedFrame.RentBgra32(16, 9) : null;
        }
        public void Release(CaptureTargetWindow window, CaptureMethod method) => Releases++;
    }

    private sealed class RegionConfigStub : IRecognitionRegionConfigService
    {
        public IReadOnlyList<string> ListConfigPaths() => [];
        public bool TryResolveConfigResolution(int width, int height, out int configWidth, out int configHeight)
        { configWidth = width; configHeight = height; return true; }
        public RecognitionRegionConfig LoadForResolution(int width, int height) => new()
        {
            LoadedFromFile = true, ResolutionWidth = width, ResolutionHeight = height,
            Regions = new[] { "magic-point", "battle-button-chat", "battle-button-skill", "battle-button-change", "battle-button-capture" }
                .Select(id => new RecognitionRegion { Id = id, Width = width, Height = height }).ToList()
        };
        public RecognitionRegionConfig LoadFromPath(string path) => throw new NotSupportedException();
        public void Save(RecognitionRegionConfig config) => throw new NotSupportedException();
        public string GetConfigPath(int width, int height) => "test";
    }

    private sealed class MatchingStub : IImageMatchingService, IDisposable
    {
        private readonly bool _temporaryTemplates;
        public ImageMatchAlgorithm DefaultAlgorithm => default;
        public string TemplateDirectory { get; }
        public volatile string? MatchedTemplate;
        public volatile int MagicPoints;
        public MatchingStub(bool temporaryTemplates = false)
        {
            _temporaryTemplates = temporaryTemplates;
            TemplateDirectory = temporaryTemplates ? Path.Combine(Path.GetTempPath(), "RocoPilot.Tests", Guid.NewGuid().ToString("N")) : "test";
            if (!temporaryTemplates) return;
            var resolutionDirectory = Path.Combine(TemplateDirectory, "16x9");
            Directory.CreateDirectory(resolutionDirectory);
            foreach (var template in new[] { "magic-point.png", "battle-chat.png", "battle-button-skill.png", "battle-button-change.png" })
                File.WriteAllBytes(Path.Combine(resolutionDirectory, template), []);
        }
        public IReadOnlyList<string> ListTemplatePaths() => [];
        public Task<ImageMatchResult> MatchAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            ImageMatchOptions? options = null, CancellationToken cancellationToken = default)
        {
            var matched = Path.GetFileName(templatePath) == MatchedTemplate;
            return Task.FromResult(new ImageMatchResult(matched, matched ? 0.99 : 0.1, 0, 0, 1, 1, templatePath));
        }
        public Task<ImageMatchCollectionResult> FindMatchesAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            int maximumMatches, ImageMatchOptions? options = null, double maximumOverlapRatio = 0.5,
            CancellationToken cancellationToken = default) => Task.FromResult(new ImageMatchCollectionResult(
                Enumerable.Range(0, MagicPoints).Select(i => new ImageMatchResult(true, 0.99, i, 0, 1, 1, templatePath)).ToArray(),
                MagicPoints > 0 ? 0.99 : 0.1, templatePath));
        public void Dispose()
        {
            if (_temporaryTemplates) Directory.Delete(TemplateDirectory, recursive: true);
        }
    }

    private sealed class HotkeyStub : IHotkeyService
    {
        public event EventHandler? SettingsChanged { add { } remove { } }
        public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered { add { } remove { } }
        public HotkeySettings Settings => new();
        public Task LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<HotkeyBindingUpdateResult> SetBindingAsync(HotkeyAction action, HotkeyBinding? binding,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class OverlayStub : IRecognitionOverlayService, IInfoOverlayService
    {
        public int Shows;
        public volatile InfoOverlaySnapshot? Snapshot;
        public void Show(RuntimeTaskState state) => Shows++;
        public void Show(RuntimeTaskState state, RecognitionRegionConfig? regionConfig = null) => Shows++;
        public void Hide() { }
        public void ResetPosition() { }
        public void SetLocked(bool isLocked) { }
        public void UpdateTaskIndicators(bool isEncounterStatisticsEnabled, bool isAutoBattleEnabled) { }
        public void UpdateSnapshot(InfoOverlaySnapshot snapshot) => Snapshot = snapshot;
        public void ShowOcrResult(string regionId, string text) { }
        public void ShowImageMatchResult(string regionId, double score) { }
    }
}
