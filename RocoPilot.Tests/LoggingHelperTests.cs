using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Helpers;

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
}
