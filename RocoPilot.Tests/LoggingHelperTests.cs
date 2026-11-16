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
                task.Debug("逐帧诊断");
                task.Information("挑战完成 {Count} 次", 2);
                task.Information("挑战完成 {Count} 次", 2);
                task.Warning(new IOException("窗口不可用"), "截图失败");
            }

            var path = directory.GetFiles("app-*.log").Single();
            var text = File.ReadAllText(path.FullName);
            Assert.IsFalse(text.Contains("逐帧诊断"));
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
    public void LogPageReceivesTheSameRecordsAsTheFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var entries = new LogEntryStore();
            using (var logger = LoggingHelper.CreateLogger(directory.FullName, entries))
            {
                var task = logger.ForContext<LoggingHelperTests>();
                task.Debug("逐帧诊断");
                task.Information("实时任务：已挂起（{Reason}），第 {Turn} 回合", "窗口最小化", 3);
                task.Warning(new IOException("窗口不可用"), "截图失败");
            }

            var records = entries.ReadFrom(0).Entries;
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(LogSeverity.Information, records[0].Severity);
            Assert.AreEqual("RocoPilot.Tests.LoggingHelperTests", records[0].Source);
            Assert.AreEqual("实时任务：已挂起（窗口最小化），第 3 回合", records[0].Message);
            Assert.IsNull(records[0].Exception);
            Assert.AreEqual(LogSeverity.Warning, records[1].Severity);
            StringAssert.StartsWith(records[1].Exception, "System.IO.IOException: 窗口不可用");

            var path = directory.GetFiles("app-*.log").Single();
            Assert.AreEqual(File.ReadAllText(path.FullName), LogEntryFormatter.Format(records));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
