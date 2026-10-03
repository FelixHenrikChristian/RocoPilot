using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;
using RocoPilot.Services.RuntimeTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class AutoBattleInputExecutorTests
{
    [TestMethod]
    public async Task ForegroundInputDoesNotSendToBackgroundWindow()
    {
        var keyboard = new KeyboardStub { Foreground = false };
        var executor = CreateExecutor(keyboard);
        var settings = new AutoBattleSettings { IsEnabled = true, KeyboardInputMethod = KeyboardInputMethod.SendInput };
        Assert.IsFalse(await executor.ExecuteAsync(1, settings, new(AutoBattleAction.Skill, "1", "", "1"), default));
        Assert.AreEqual(0, keyboard.Sends);
        settings.KeyboardInputMethod = KeyboardInputMethod.PostMessage;
        Assert.IsTrue(await executor.ExecuteAsync(1, settings, new(AutoBattleAction.Skill, "1", "", "1"), default));
    }

    [TestMethod]
    public async Task InvalidSkillTemplateFallsBackButInvalidCustomSequenceDoesNot()
    {
        var keyboard = new KeyboardStub();
        var executor = CreateExecutor(keyboard);
        var settings = new AutoBattleSettings();
        Assert.IsTrue(await executor.ExecuteAsync(1, settings, new(AutoBattleAction.Skill, "invalid", "", "1", "1"), default));
        Assert.IsFalse(await executor.ExecuteAsync(1, settings, new(AutoBattleAction.Skill, "invalid", "", "组合"), default));
        Assert.AreEqual(1, keyboard.Sends);
    }

    [TestMethod]
    public async Task CaptureUsesCaptureTimingAndManualWaitSendsNothing()
    {
        var keyboard = new KeyboardStub();
        var executor = CreateExecutor(keyboard);
        var settings = new AutoBattleSettings { KeyboardIntervalMs = 100, CaptureKeyboardIntervalMs = 700 };
        Assert.IsTrue(await executor.ExecuteAsync(1, settings, new(AutoBattleAction.Capture, "W, 1, Space", "", ""), default));
        Assert.AreEqual(700, keyboard.LastOptions?.IntervalMs);
        Assert.IsFalse(await executor.ExecuteAsync(1, settings, new(AutoBattleAction.NoAction, "", "", ""), default));
        Assert.AreEqual(1, keyboard.Sends);
    }

    private static AutoBattleInputExecutor CreateExecutor(KeyboardStub keyboard) => new(keyboard, NullLogger<AutoBattleInputExecutor>.Instance);

    [TestMethod]
    public async Task ProgressReportsTheActualFallbackAndOnlyCompletesAfterSending()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyboard = new KeyboardStub { SendTask = sent.Task };
        var updates = new List<(string Sequence, bool Completed)>();
        var execution = CreateExecutor(keyboard).ExecuteAsync(1, new(),
            new(AutoBattleAction.Skill, "invalid", "", "1", "1"), default,
            (sequence, completed) => updates.Add((sequence, completed)));
        CollectionAssert.AreEqual(new[] { ("1", false) }, updates);
        Assert.IsFalse(execution.IsCompleted);
        sent.SetResult();
        Assert.IsTrue(await execution);
        CollectionAssert.AreEqual(new[] { ("1", false), ("1", true) }, updates);
    }

    [TestMethod]
    public async Task DisplayFailureCannotPreventActualKeySending()
    {
        var keyboard = new KeyboardStub();
        Assert.IsTrue(await CreateExecutor(keyboard).ExecuteAsync(1, new(),
            new(AutoBattleAction.Skill, "1", "", "1"), default,
            (_, _) => throw new InvalidOperationException("display failed")));
        Assert.AreEqual(1, keyboard.Sends);
    }

    [TestMethod]
    public async Task FailedOrCancelledSendingNeverReportsCompletion()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("send failed"), new OperationCanceledException() })
        {
            var keyboard = new KeyboardStub { SendTask = Task.FromException(failure) };
            var updates = new List<bool>();
            try
            {
                await CreateExecutor(keyboard).ExecuteAsync(1, new(), new(AutoBattleAction.Skill, "1", "", "1"), default,
                    (_, completed) => updates.Add(completed));
                Assert.Fail("Expected sending failure");
            }
            catch (Exception ex) when (ReferenceEquals(ex, failure)) { }
            CollectionAssert.AreEqual(new[] { false }, updates);
        }
    }

    [TestMethod]
    public async Task InvalidSequenceOrBackgroundWindowDoesNotPublishExecution()
    {
        var keyboard = new KeyboardStub();
        var updates = 0;
        var executor = CreateExecutor(keyboard);
        Assert.IsFalse(await executor.ExecuteAsync(1, new(), new(AutoBattleAction.Skill, "invalid", "", "组合"), default,
            (_, _) => updates++));
        keyboard.Foreground = false;
        Assert.IsFalse(await executor.ExecuteAsync(1, new() { KeyboardInputMethod = KeyboardInputMethod.SendInput },
            new(AutoBattleAction.Skill, "1", "", "1"), default, (_, _) => updates++));
        Assert.AreEqual(0, updates);
        Assert.AreEqual(0, keyboard.Sends);
    }

    private sealed class KeyboardStub : IKeyboardInputService
    {
        public bool Foreground = true;
        public int Sends;
        public KeyboardInputOptions? LastOptions;
        public Task SendTask = Task.CompletedTask;
        public bool IsWindowAvailable(nint hwnd) => true;
        public bool IsWindowForeground(nint hwnd) => Foreground;
        public bool RequiresForeground(KeyboardInputMethod method) => method != KeyboardInputMethod.PostMessage;
        public void EnsureReady(KeyboardInputMethod method) { }
        public bool TryParseSequence(string sequence, out IReadOnlyList<KeyStroke> strokes, out string error)
        {
            strokes = sequence == "invalid" ? [] : [new KeyStroke([], new KeyDefinition("1", 49))];
            error = strokes.Count == 0 ? "invalid" : "";
            return strokes.Count > 0;
        }
        public Task SendSequenceAsync(nint hwnd, string sequence, KeyboardInputOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SendSequenceAsync(nint hwnd, IReadOnlyList<KeyStroke> strokes, KeyboardInputOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sends++;
            LastOptions = options;
            return SendTask;
        }
    }
}
