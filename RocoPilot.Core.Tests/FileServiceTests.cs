using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Core.Services;

namespace RocoPilot.Core.Tests;

[TestClass]
public sealed class FileServiceTests
{
    [TestMethod]
    public void ReplacingAFileLeavesExistingReaderOnTheCompleteOldDocument()
    {
        var directory = Directory.CreateTempSubdirectory("RocoPilot-file-test-");
        try
        {
            var files = new FileService();
            files.Save(directory.FullName, "settings.json", new { Count = 1 });
            using (var stream = new FileStream(Path.Combine(directory.FullName, "settings.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                files.Save(directory.FullName, "settings.json", new { Count = 2 });
                using var reader = new StreamReader(stream);
                Assert.AreEqual("{\"Count\":1}", reader.ReadToEnd());
                Assert.AreEqual(2, files.Read<Dictionary<string, int>>(directory.FullName, "settings.json")["Count"]);
            }
            Assert.AreEqual(1, directory.GetFiles().Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public void FailedReplacementPreservesOriginalAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("此场景验证 Windows 文件共享锁。");
        var directory = Directory.CreateTempSubdirectory("RocoPilot-file-test-");
        try
        {
            var files = new FileService();
            files.Save(directory.FullName, "settings.json", new { Count = 1 });
            using (var locked = new FileStream(Path.Combine(directory.FullName, "settings.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsExactly<IOException>(() => files.Save(directory.FullName, "settings.json", new { Count = 2 }));
            }
            Assert.AreEqual(1, files.Read<Dictionary<string, int>>(directory.FullName, "settings.json")["Count"]);
            Assert.AreEqual(1, directory.GetFiles().Length);
        }
        finally { directory.Delete(recursive: true); }
    }
}
