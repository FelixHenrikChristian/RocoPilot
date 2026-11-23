using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

/// <summary>每日页的展示状态：清单摘要、正在执行的一项、右侧配置区的空状态。</summary>
[TestClass]
public sealed class DailyViewModelTests
{
    private sealed class Store : ILocalSettingsService
    {
        public Dictionary<string, object?> Data { get; } = [];
        public int Saves;
        public Task<T?> ReadSettingAsync<T>(string key) => Task.FromResult(Data.TryGetValue(key, out var v) ? (T?)v : default);
        public Task SaveSettingAsync<T>(string key, T value) { Data[key] = value; Saves++; return Task.CompletedTask; }
        public Task ResetAllAsync() => Task.CompletedTask;
    }

    private sealed class Tasks : IIndependentTaskService
    {
        public event EventHandler? StateChanged;
        public bool IsRunning { get; set; }
        public bool IsDailyRunning { get; set; }
        public string StatusMessage { get; set; } = "就绪";
        public IndependentTaskKind? RunningTaskKind { get; set; }
        public IndependentTaskSettings Settings { get; } = IndependentTaskSettings.CreateDefault();
        public IReadOnlyList<string> LegendaryTargets { get; } = ["圣羽翼王"];
        public IReadOnlyList<IndependentTaskKind>? Started;
        public Task LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetSettings(IndependentTaskSettings settings) { }
        public Task<IndependentTaskStartResult> StartAsync(IndependentTaskKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync() { Set(false, false, null); return Task.CompletedTask; }
        public Task<IndependentTaskStartResult> StartDailyAsync(IReadOnlyList<IndependentTaskKind> tasks, IndependentTaskSettings challenges, CancellationToken cancellationToken = default)
        {
            Started = tasks;
            IsRunning = IsDailyRunning = true;
            RunningTaskKind = tasks[0];
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(IndependentTaskStartResult.Started("ok"));
        }
        public void Set(bool running, bool daily, IndependentTaskKind? kind)
        {
            IsRunning = running; IsDailyRunning = daily; RunningTaskKind = kind;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<(DailyViewModel Vm, Tasks Tasks, Store Store)> CreateAsync(params DailyTaskEntry[] entries)
    {
        var store = new Store();
        if (entries.Length > 0) store.Data[SettingsKeys.DailyTaskSettings] = new DailyTaskSettings { Tasks = [.. entries] };
        var tasks = new Tasks();
        var vm = new DailyViewModel(tasks, store, NullLogger<DailyViewModel>.Instance);
        await vm.LoadAsync();
        return (vm, tasks, store);
    }

    [TestMethod]
    public async Task EveryTaskHasADistinctGlyphFromTheIconFont()
    {
        var (vm, _, _) = await CreateAsync();
        Assert.AreEqual(Enum.GetValues<DailyTaskKind>().Length, vm.Items.Count);
        var glyphs = vm.Items.Select(i => i.Glyph).ToList();
        Assert.AreEqual(glyphs.Count, glyphs.Distinct().Count(), "图标不应重复");
        foreach (var item in vm.Items)
        {
            Assert.AreEqual(1, item.Glyph.Length, item.Title);
            Assert.AreNotEqual(0xEC92, item.Glyph[0], $"{item.Title} 落到了默认图标");
            Assert.AreNotEqual(item.IsAvailable, item.IsUnavailable);
        }
    }

    [TestMethod]
    public async Task SummaryCountsEnabledTasksAndEmptyStateFollowsConfigurableOnes()
    {
        var (vm, _, _) = await CreateAsync();
        Assert.AreEqual("未启用任务", vm.ListSummary);
        Assert.IsTrue(vm.IsConfigurationEmpty);
        Assert.IsFalse(vm.CanStartOrStop);

        vm.Items.Single(i => i.Kind == DailyTaskKind.Mail).IsEnabled = true;
        Assert.AreEqual("已启用 1 项", vm.ListSummary);
        Assert.IsTrue(vm.IsConfigurationEmpty, "邮件领取没有配置项");
        Assert.IsTrue(vm.CanStartOrStop);

        vm.Items.Single(i => i.Kind == DailyTaskKind.Sell).IsEnabled = true;
        Assert.AreEqual("已启用 2 项", vm.ListSummary);
        Assert.IsFalse(vm.IsConfigurationEmpty);

        vm.Items.Single(i => i.Kind == DailyTaskKind.Sell).IsEnabled = false;
        Assert.IsTrue(vm.IsConfigurationEmpty);
    }

    [TestMethod]
    public async Task UnavailableTaskIsNeverCountedEvenIfSavedAsEnabled()
    {
        var (vm, _, _) = await CreateAsync(new(DailyTaskKind.Inspiration, true), new(DailyTaskKind.Mail, true));
        Assert.IsFalse(vm.Items[0].IsEnabled);
        Assert.IsTrue(vm.Items[0].IsUnavailable);
        Assert.AreEqual("已启用 1 项", vm.ListSummary);
    }

    [TestMethod]
    public async Task RunningMarksOnlyTheCurrentItemAndReportsItsPosition()
    {
        var (vm, tasks, _) = await CreateAsync(
            new(DailyTaskKind.Mail, true), new(DailyTaskKind.StaminaCrafting), new(DailyTaskKind.FlowerSeedChallenge, true),
            new(DailyTaskKind.Sell, true));
        await vm.ToggleRunCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { IndependentTaskKind.Mail, IndependentTaskKind.FlowerSeedChallenge, IndependentTaskKind.Sell },
            tasks.Started!.ToArray());
        Assert.IsTrue(vm.IsRunning);
        Assert.AreEqual("停止", vm.StartButtonLabel);
        Assert.AreEqual("正在执行第 1 / 3 项", vm.ListSummary);
        Assert.AreEqual(DailyTaskKind.Mail, vm.Items.Single(i => i.IsRunning).Kind);

        // 跳过未启用的体力合成：花种挑战是第 2 项，不是第 3 项。
        tasks.Set(true, true, IndependentTaskKind.FlowerSeedChallenge);
        Assert.AreEqual("正在执行第 2 / 3 项", vm.ListSummary);
        Assert.AreEqual(DailyTaskKind.FlowerSeedChallenge, vm.Items.Single(i => i.IsRunning).Kind);

        // 未启用的任务即便种类相同也不标记。
        tasks.Set(true, true, IndependentTaskKind.StaminaCrafting);
        Assert.AreEqual(0, vm.Items.Count(i => i.IsRunning));
        Assert.AreEqual("正在执行", vm.ListSummary);

        tasks.Set(true, true, IndependentTaskKind.Sell);
        Assert.AreEqual("正在执行第 3 / 3 项", vm.ListSummary);

        tasks.Set(false, false, null);
        Assert.IsFalse(vm.IsRunning);
        Assert.AreEqual("启动", vm.StartButtonLabel);
        Assert.AreEqual(0, vm.Items.Count(i => i.IsRunning));
        Assert.AreEqual("已启用 3 项", vm.ListSummary);
    }

    [TestMethod]
    public async Task AnotherTaskRunningExplainsWhyStartIsDisabled()
    {
        var (vm, tasks, _) = await CreateAsync(new DailyTaskEntry(DailyTaskKind.LegendaryChallenge, true));
        tasks.Set(true, false, IndependentTaskKind.LegendaryChallenge);
        Assert.IsFalse(vm.IsRunning);
        Assert.IsFalse(vm.CanStartOrStop);
        Assert.AreEqual("其他任务运行中", vm.ListSummary);
        Assert.AreEqual(0, vm.Items.Count(i => i.IsRunning), "任务页单独启动的挑战不算每日清单在执行");
        Assert.IsFalse(vm.IsConfigurationEmpty);
    }

    [TestMethod]
    public async Task PresentationChangesDoNotTriggerExtraSaves()
    {
        var (vm, tasks, store) = await CreateAsync(new DailyTaskEntry(DailyTaskKind.Mail, true));
        var saves = store.Saves;
        tasks.Set(true, true, IndependentTaskKind.Mail);
        tasks.Set(false, false, null);
        Assert.AreEqual(saves, store.Saves);
    }
}
