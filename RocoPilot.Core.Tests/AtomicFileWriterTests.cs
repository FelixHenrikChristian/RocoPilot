using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Core.Services;

namespace RocoPilot.Core.Tests;

[TestClass]
public sealed class AtomicFileWriterTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedReplacementPreservesOldContentsAndRemovesTemporaryFile(bool binary)
    {
        var directory = Directory.CreateTempSubdirectory("RocoPilot-atomic-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "data");
            await File.WriteAllTextAsync(path, "old");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsExactlyAsync<IOException>(() => WriteAsync(path, binary));
            Assert.AreEqual("old", await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, directory.GetFiles().Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationDoesNotTouchOriginalFile(bool binary)
    {
        var directory = Directory.CreateTempSubdirectory("RocoPilot-atomic-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "data");
            await File.WriteAllTextAsync(path, "old");
            await Assert.ThrowsAsync<OperationCanceledException>(() => WriteAsync(path, binary, new CancellationToken(true)));
            Assert.AreEqual("old", await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, directory.GetFiles().Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementKeepsOpenReaderOnOldVersion(bool binary)
    {
        var directory = Directory.CreateTempSubdirectory("RocoPilot-atomic-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "nested", "data");
            await AtomicFileWriter.WriteAllTextAsync(path, "old");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await WriteAsync(path, binary);
                using var reader = new StreamReader(stream);
                Assert.AreEqual("old", await reader.ReadToEndAsync());
                Assert.AreEqual("new", await File.ReadAllTextAsync(path));
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static Task WriteAsync(string path, bool binary, CancellationToken token = default) => binary
        ? AtomicFileWriter.WriteAllBytesAsync(path, "new"u8.ToArray(), token)
        : AtomicFileWriter.WriteAllTextAsync(path, "new", token);
}
