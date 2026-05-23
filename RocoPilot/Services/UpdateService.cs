using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Views;

using Windows.ApplicationModel;

namespace RocoPilot.Services;

public sealed class UpdateService : IUpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/FelixHenrikChristian/RocoPilot/releases/latest";
    private const string DownloadPageUrl = "https://github.com/FelixHenrikChristian/RocoPilot/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckUpdateAsync(UpdateOption option)
    {
        _logger.LogInformation("开始检查更新 (触发方式: {Trigger})", option.Trigger);

        try
        {
            var latestRelease = await GetLatestReleaseAsync();
            if (latestRelease == null)
            {
                return UpdateCheckResult.Failed("无法获取最新版本信息，请稍后再试。");
            }

            if (!TryParseReleaseVersion(latestRelease.TagName, out var latestVersion))
            {
                _logger.LogWarning("Release 标签版本号无法解析: {TagName}", latestRelease.TagName);
                return UpdateCheckResult.Failed($"最新版本号格式无法识别：{latestRelease.TagName}");
            }

            var currentVersion = GetCurrentAppVersion();
            _logger.LogInformation("当前版本: {Current}, 最新版本: {Latest}", currentVersion, latestVersion);

            if (latestVersion <= currentVersion)
            {
                return UpdateCheckResult.UpToDate("当前已安装最新版本。");
            }

            return await HandleUpdateDialogResultAsync(latestRelease);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查更新时发生错误");
            return UpdateCheckResult.Failed("检查更新失败，请检查网络连接后重试。");
        }
    }

    private async Task<UpdateCheckResult> HandleUpdateDialogResultAsync(GitHubRelease latestRelease)
    {
        var updateResult = await ShowUpdateDialogAsync(latestRelease);
        switch (updateResult)
        {
            case UpdateWindow.UpdateResult.Update:
            {
                return StartUpdater()
                    ? UpdateCheckResult.UpdateAvailable(latestRelease, $"正在启动更新程序：{latestRelease.TagName}")
                    : UpdateCheckResult.Failed("未找到更新程序，已打开 GitHub 下载页面。");
            }

            case UpdateWindow.UpdateResult.Download:
                OpenDownloadPage();
                return UpdateCheckResult.UpdateAvailable(latestRelease, $"已打开下载页面：{latestRelease.TagName}");

            default:
                return UpdateCheckResult.UpdateAvailable(latestRelease, $"发现新版本 {latestRelease.TagName}。");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RocoPilot");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool TryParseReleaseVersion(string tagName, out Version version)
    {
        var normalized = tagName.Trim().TrimStart('v', 'V');
        var metadataStart = normalized.IndexOfAny(new[] { '-', '+' });
        if (metadataStart >= 0)
        {
            normalized = normalized[..metadataStart];
        }

        var success = Version.TryParse(normalized, out var parsedVersion);
        version = parsedVersion ?? new Version(0, 0, 0, 0);
        return success;
    }

    private static Version GetCurrentAppVersion()
    {
        if (RuntimeHelper.IsMSIX)
        {
            var packageVersion = Package.Current.Id.Version;
            return new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }

        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync()
    {
        try
        {
            using var response = await HttpClient.GetAsync(GitHubApiUrl);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub API 请求失败: {StatusCode} {Reason} Body: {Body}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    json);
                return null;
            }

            return JsonSerializer.Deserialize<GitHubRelease>(json);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "请求 GitHub API 失败");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "请求 GitHub API 超时");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "解析 GitHub API 返回的 JSON 失败");
            return null;
        }
    }

    private async Task<UpdateWindow.UpdateResult> ShowUpdateDialogAsync(GitHubRelease release)
    {
        var taskCompletionSource =
            new TaskCompletionSource<UpdateWindow.UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherQueue = App.MainWindow.DispatcherQueue;

        if (!dispatcherQueue.TryEnqueue(() => _ = ShowUpdateWindowAsync()))
        {
            throw new InvalidOperationException("无法将更新窗口调度到 UI 线程。");
        }

        return await taskCompletionSource.Task;

        async Task ShowUpdateWindowAsync()
        {
            try
            {
                var updateWindow = new UpdateWindow(release);
                var result = await updateWindow.ShowAsync();
                taskCompletionSource.TrySetResult(result);
            }
            catch (Exception ex)
            {
                taskCompletionSource.TrySetException(ex);
            }
        }
    }

    private bool StartUpdater()
    {
        try
        {
            var updaterExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RocoPilot.update.exe");
            if (!File.Exists(updaterExePath))
            {
                _logger.LogWarning("更新程序不存在: {Path}", updaterExePath);
                OpenDownloadPage();
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterExePath,
                UseShellExecute = true,
            });
            Application.Current.Exit();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动更新程序失败");
            OpenDownloadPage();
            return false;
        }
    }

    private void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadPageUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开下载页面失败");
        }
    }
}
