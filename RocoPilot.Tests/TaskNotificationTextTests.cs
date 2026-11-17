using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.IndependentTasks;

namespace RocoPilot.Tests;

[TestClass]
public sealed class TaskNotificationTextTests
{
    [TestMethod]
    public void ChallengeCompletionPutsTargetAndCountInTheBody()
    {
        Assert.AreEqual(("传说精灵挑战完成", "圣羽翼王 · 成功 5 次"),
            TaskNotificationText.ChallengeCompleted("传说精灵挑战", "圣羽翼王", 5));
        Assert.AreEqual(("花种挑战完成", "成功 6 次"), TaskNotificationText.ChallengeCompleted("花种挑战", null, 6));
    }

    [TestMethod]
    [DataRow("游戏窗口已关闭。", "游戏窗口已关闭。")]
    [DataRow("测试失败原因", "测试失败原因。")]
    [DataRow("点击开始：输入 3 次后目标仍未消失（2048x1152/flower-seed/start.png）。", "点击开始：输入 3 次后目标仍未消失。")]
    [DataRow("点击挑战（2048x1152/flower-seed/challenge.png，已输入 2 次）：10 秒内未确认原界面目标消失。",
        "点击挑战：10 秒内未确认原界面目标消失。")]
    [DataRow("挑战连续失败 3 次，任务已停止。请检查精灵状态后重新启动。", "挑战连续失败 3 次，任务已停止。请检查精灵状态后重新启动。")]
    [DataRow("队伍不足（等待确认弹窗状态）", "队伍不足（等待确认弹窗状态）。")]
    [DataRow("", "原因见日志页。")]
    public void ReasonDropsTemplatePathsAndEndsWithOnePeriod(string message, string expected)
    {
        Assert.AreEqual(("花种挑战未完成", expected), TaskNotificationText.TaskFailed("花种挑战", message));
    }

    [TestMethod]
    public void DailySummaryFitsTheNotificationBody()
    {
        (string, string?)[] items =
        [
            ("邮件领取", "成功：已领取邮件附件"), ("自动种菜", "种了 12/15"), ("通行证奖励", "通行证任务领取了3/4"),
            ("体力合成", "跳过：体力不足"), ("晚风小憩", "跳过：未找到前往按钮")
        ];
        Assert.AreEqual(("每日清单完成", "完成 3 项，跳过 2 项。\n跳过：体力合成（体力不足）、晚风小憩（未找到前往按钮）"),
            TaskNotificationText.DailyCompleted(items));
        Assert.AreEqual(("每日清单完成", "共 2 项，全部完成。"), TaskNotificationText.DailyCompleted(items[..2]));
    }

    [TestMethod]
    public void DailyFailureStatesTheReasonOnce()
    {
        (string, string?)[] items =
        [
            ("邮件领取", "成功：已领取邮件附件"), ("体力合成", "跳过：体力不足"),
            ("自动种菜", "失败：游戏窗口已关闭。"), ("通行证奖励", null)
        ];
        Assert.AreEqual(("每日清单未完成", "自动种菜：游戏窗口已关闭。\n已完成 1 项，跳过 1 项，未执行 1 项。"),
            TaskNotificationText.DailyFailed("自动种菜", "游戏窗口已关闭。", items));
        Assert.AreEqual(("每日清单未完成", "邮件领取：运行会话已结束。"),
            TaskNotificationText.DailyFailed("邮件领取", "运行会话已结束。", [("邮件领取", "失败：运行会话已结束。")]));
    }
}
