using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

    private static RuntimeTaskStartOptions Options(bool enabled = true,
        KeyboardInputMethod method = KeyboardInputMethod.Interception) => new()
    {
        AutoBattleSettings = new() { IsEnabled = enabled, KeyboardInputMethod = method }
    };

    private sealed class StartupFixture
    {
        public WindowStub Window { get; } = new();
        public KeyboardStub Keyboard { get; } = new();
        public CaptureStub Capture { get; } = new();
        public OverlayStub Overlay { get; } = new();
        public RuntimeTaskService Runtime { get; }

        public StartupFixture()
        {
            var settings = new ControlledSettingsStore();
            var matching = new MatchingStub();
            var debug = new RuntimeDebugLogger(NullLogger<RuntimeDebugLogger>.Instance);
            // No frames are published by the workers, so these tests never run OCR.
            var recognizer = new RuntimeFrameRecognizer(matching, null!, Overlay, debug);
            Runtime = new(Window, Keyboard, Capture, new RegionConfigStub(), matching, recognizer,
                new BattleScreenRecognizer(recognizer),
                new AutoBattleInputExecutor(Keyboard, NullLogger<AutoBattleInputExecutor>.Instance), debug,
                new StatisticsSeasonConfigStub(), new StatisticsSpiritCatalogStub(),
                new StatisticsService(settings, NullLogger<StatisticsService>.Instance), settings,
                new HotkeyStub(), Overlay, Overlay, NullLogger<RuntimeTaskService>.Instance);
            Capture.BeforeCapture = () =>
            {
                Assert.AreEqual(0, Overlay.Shows, "启动前截图必须发生在创建遮罩之前。");
            };
        }
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
        public CapturedFrame? Capture(CaptureTargetWindow window, CaptureMethod method)
        {
            if (Interlocked.Increment(ref Calls) != 1) return null;
            BeforeCapture?.Invoke();
            return CapturedFrame.RentBgra32(16, 9);
        }
        public void Release(CaptureTargetWindow window, CaptureMethod method) => Releases++;
    }

    private sealed class RegionConfigStub : IRecognitionRegionConfigService
    {
        public IReadOnlyList<string> ListConfigPaths() => [];
        public bool TryResolveConfigResolution(int width, int height, out int configWidth, out int configHeight)
        { configWidth = width; configHeight = height; return true; }
        public RecognitionRegionConfig LoadForResolution(int width, int height) => new()
        { LoadedFromFile = true, Regions = [new() { Id = "test" }], ResolutionWidth = width, ResolutionHeight = height };
        public RecognitionRegionConfig LoadFromPath(string path) => throw new NotSupportedException();
        public void Save(RecognitionRegionConfig config) => throw new NotSupportedException();
        public string GetConfigPath(int width, int height) => "test";
    }

    private sealed class MatchingStub : IImageMatchingService
    {
        public ImageMatchAlgorithm DefaultAlgorithm => default;
        public string TemplateDirectory => "test";
        public IReadOnlyList<string> ListTemplatePaths() => [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDefaultAlgorithmAsync(ImageMatchAlgorithm algorithm, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageMatchResult> MatchAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            ImageMatchOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageMatchCollectionResult> FindMatchesAsync(CapturedFrame frame, RecognitionRegion region, string templatePath,
            int maximumMatches, ImageMatchOptions? options = null, double maximumOverlapRatio = 0.5,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        public void Show(RuntimeTaskState state) => Shows++;
        public void Hide() { }
        public void ResetPosition() { }
        public void SetLocked(bool isLocked) { }
        public void UpdateTaskIndicators(bool isEncounterStatisticsEnabled, bool isAutoBattleEnabled) { }
        public void UpdateSnapshot(InfoOverlaySnapshot snapshot) { }
        public void ShowOcrResult(string regionId, string text) { }
        public void ShowImageMatchResult(string regionId, double score) { }
    }
}
