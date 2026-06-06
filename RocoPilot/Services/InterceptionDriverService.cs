using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;

using InterceptionInput = InputInterceptorNS.InputInterceptor;

namespace RocoPilot.Services;

public sealed class InterceptionDriverService : IInterceptionDriverService
{
    private const string ArchiveFileName = "Interception.zip";
    private const string InstallerFileName = "install-interception.exe";
    private const int ErrorCancelled = 1223;

    private static readonly Uri DownloadUri = new(
        $"https://github.com/oblitum/Interception/releases/latest/download/{ArchiveFileName}");

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly ILogger<InterceptionDriverService> _logger;

    public Uri ReleasePageUri { get; } = new("https://github.com/oblitum/Interception/releases/latest");

    public InterceptionDriverService(ILogger<InterceptionDriverService> logger)
    {
        _logger = logger;
    }

    public bool IsDriverInstalled()
    {
        try
        {
            return InterceptionInput.CheckDriverInstalled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检测 Interception 驱动安装状态失败");
            return false;
        }
    }

    public async Task<InterceptionDriverInstallResult> InstallAsync(
        IProgress<InterceptionDriverInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsDriverInstalled())
        {
            progress?.Report(new InterceptionDriverInstallProgress("Interception 驱动已安装。", 100));
            return new InterceptionDriverInstallResult(true, string.Empty);
        }

        var packageDirectory = GetPackageDirectory();
        var archivePath = Path.Combine(packageDirectory, ArchiveFileName);
        var extractDirectory = Path.Combine(packageDirectory, "latest", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(extractDirectory);

        progress?.Report(new InterceptionDriverInstallProgress("正在下载 Interception 安装包..."));
        await DownloadArchiveAsync(archivePath, progress, cancellationToken);

        progress?.Report(new InterceptionDriverInstallProgress("正在解压安装包..."));
        ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);

        var installerPath = FindInstallerPath(extractDirectory);
        if (installerPath is null)
        {
            throw new FileNotFoundException("安装包中未找到 install-interception.exe。", InstallerFileName);
        }

        progress?.Report(new InterceptionDriverInstallProgress("正在启动驱动安装程序，请在系统弹窗中允许管理员权限..."));
        await RunInstallerAsync(installerPath, cancellationToken);

        progress?.Report(new InterceptionDriverInstallProgress("安装命令已完成，需要重启电脑后生效。", 100));
        return new InterceptionDriverInstallResult(false, installerPath);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RocoPilot");
        return client;
    }

    private static string GetPackageDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "RocoPilot", "Interception");
    }

    private static async Task DownloadArchiveAsync(
        string archivePath,
        IProgress<InterceptionDriverInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (contentLength is > 0)
            {
                var percent = Math.Min(99, totalRead * 100d / contentLength.Value);
                progress?.Report(new InterceptionDriverInstallProgress(
                    $"正在下载 Interception 安装包... {percent:0}%",
                    percent));
            }
        }
    }

    private static string? FindInstallerPath(string extractDirectory)
    {
        return Directory
            .EnumerateFiles(extractDirectory, InstallerFileName, SearchOption.AllDirectories)
            .OrderByDescending(path => path.Contains("command line installer", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static async Task RunInstallerAsync(string installerPath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/install",
                WorkingDirectory = Path.GetDirectoryName(installerPath),
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("无法启动 Interception 驱动安装程序。");

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Interception 驱动安装程序返回错误码：{process.ExitCode}。");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("用户取消了管理员权限授权。", ex, cancellationToken);
        }
    }
}
