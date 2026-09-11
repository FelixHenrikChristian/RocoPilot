using System.Text;

namespace RocoPilot.Core.Services;

public static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content, Encoding encoding)
    {
        using var pending = new PendingFile(path);
        File.WriteAllText(pending.TemporaryPath, content, encoding);
        pending.Commit();
    }

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pending = new PendingFile(path);
        await File.WriteAllTextAsync(pending.TemporaryPath, content, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        pending.Commit();
    }

    public static async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pending = new PendingFile(path);
        await File.WriteAllBytesAsync(pending.TemporaryPath, content, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        pending.Commit();
    }

    private sealed class PendingFile : IDisposable
    {
        private readonly string _targetPath;
        public string TemporaryPath { get; }

        public PendingFile(string path)
        {
            _targetPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(_targetPath)!;
            Directory.CreateDirectory(directory);
            TemporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        }

        public void Commit()
        {
            // 同目录替换保留旧文件，直到新内容完整写入；已打开的读者继续读取旧版本。
            if (File.Exists(_targetPath)) File.Replace(TemporaryPath, _targetPath, destinationBackupFileName: null);
            else File.Move(TemporaryPath, _targetPath);
        }

        public void Dispose()
        {
            if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath);
        }
    }
}
