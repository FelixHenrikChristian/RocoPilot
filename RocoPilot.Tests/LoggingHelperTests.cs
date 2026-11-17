using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Helpers;
using RocoPilot.Models.Logging;
using RocoPilot.Services.Logging;

namespace RocoPilot.Tests;

[TestClass]
public sealed class LoggingHelperTests
{
    [TestMethod]
    public void FileLogKeepsBusinessEventsAndExceptionsWithoutDebugNoise()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            using (var logger = LoggingHelper.CreateLogger(directory.FullName))
            {
                var task = logger.ForContext<LoggingHelperTests>();
                task.Verbose("逐次识别");
                task.Debug("识别变化");
                task.Information("挑战完成 {Count} 次", 2);
                task.Information("挑战完成 {Count} 次", 2);
                task.Warning(new IOException("窗口不可用"), "截图失败");
            }

            var path = directory.GetFiles(LogPipeline.AppFilePattern).Single();
            var text = File.ReadAllText(path.FullName);
            Assert.IsFalse(text.Contains("逐次识别"));
            Assert.IsFalse(text.Contains("识别变化"));
            Assert.AreEqual(2, File.ReadLines(path.FullName).Count(line => line.Contains("挑战完成 2 次")));
            StringAssert.Contains(text, "[WRN] [RocoPilot.Tests.LoggingHelperTests] 截图失败");
            StringAssert.Contains(text, "System.IO.IOException: 窗口不可用");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void DebugFileKeepsDebugRecordsAndReplaysFrameRecordsBeforeAWarning()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            using (var logger = LoggingHelper.CreateLogger(directory.FullName))
            {
                var task = logger.ForContext<LoggingHelperTests>();
                task.Verbose("逐次识别 {Index}", 1);
                task.Debug("识别变化");
                task.Information("进入战斗");
                Assert.IsFalse(ReadDebugFile(directory).Contains("逐次识别 1"));

                task.Warning(new IOException("窗口不可用"), "截图失败");
            }

            var lines = ReadDebugFile(directory).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            StringAssert.EndsWith(lines[0], "[DBG] [RocoPilot.Tests.LoggingHelperTests] 识别变化");
            StringAssert.EndsWith(lines[1], "[INF] [RocoPilot.Tests.LoggingHelperTests] 进入战斗");
            StringAssert.EndsWith(lines[2],
                "[DBG] [RocoPilot.Services.Logging.FrameTraceSink] 回放开始：以下 1 条是此前最近的逐次识别记录");
            StringAssert.EndsWith(lines[3], "[VRB] [RocoPilot.Tests.LoggingHelperTests] 逐次识别 1");
            StringAssert.EndsWith(lines[4], "[DBG] [RocoPilot.Services.Logging.FrameTraceSink] 回放结束");
            StringAssert.EndsWith(lines[5], "[WRN] [RocoPilot.Tests.LoggingHelperTests] 截图失败");
            StringAssert.StartsWith(lines[6], "System.IO.IOException: 窗口不可用");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void DebugFileCanBeLeftOut()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            using (var logger = LoggingHelper.CreateLogger(directory.FullName, writeDebugFile: false))
            {
                logger.ForContext<LoggingHelperTests>().Error("热键辅助进程异常退出");
            }

            Assert.AreEqual(0, directory.GetFiles(LogPipeline.DebugFilePattern).Length);
            StringAssert.Contains(
                File.ReadAllText(directory.GetFiles(LogPipeline.AppFilePattern).Single().FullName),
                "[ERR] [RocoPilot.Tests.LoggingHelperTests] 热键辅助进程异常退出");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void LogPageKeepsDebugRecordsAndItsOtherRecordsMatchTheFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var entries = new LogEntryStore();
            using (var logger = LoggingHelper.CreateLogger(directory.FullName, entries))
            {
                var task = logger.ForContext<LoggingHelperTests>();
                task.Verbose("逐次识别");
                task.Debug("识别变化 {Target}", "battle-chat");
                task.Information("实时任务：已挂起（{Reason}），第 {Turn} 回合", "窗口最小化", 3);
                task.Warning(new IOException("窗口不可用"), "截图失败");
            }

            var records = entries.ReadFrom(0).Entries;
            Assert.AreEqual(3, records.Count);
            Assert.AreEqual(LogSeverity.Debug, records[0].Severity);
            Assert.AreEqual("识别变化 battle-chat", records[0].Message);
            Assert.AreEqual(LogSeverity.Information, records[1].Severity);
            Assert.AreEqual("RocoPilot.Tests.LoggingHelperTests", records[1].Source);
            Assert.AreEqual("实时任务：已挂起（窗口最小化），第 3 回合", records[1].Message);
            Assert.IsNull(records[1].Exception);
            Assert.AreEqual(LogSeverity.Warning, records[2].Severity);
            StringAssert.StartsWith(records[2].Exception, "System.IO.IOException: 窗口不可用");

            // 日志页默认显示的记录与 app 日志文件逐字一致。
            var path = directory.GetFiles(LogPipeline.AppFilePattern).Single();
            Assert.AreEqual(
                File.ReadAllText(path.FullName),
                LogEntryFormatter.Format(records.Where(record => record.Severity != LogSeverity.Debug)));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    // 日志文件以共享方式打开，写入期间也能读到已经落盘的内容。
    private static string ReadDebugFile(DirectoryInfo directory)
    {
        var path = directory.GetFiles(LogPipeline.DebugFilePattern).Single().FullName;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
