using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly UpdateSourceOptions _sourceOptions;

    public UpdateService(ILogger<UpdateService> logger, IOptions<UpdateSourceOptions> sourceOptions)
    {
        _logger = logger;
        _sourceOptions = sourceOptions.Value;
    }

    public async Task<UpdateCheckResult> CheckUpdateAsync(UpdateOption option)
    {
        _logger.LogDebug("开始检查更新（触发方式：{Trigger}）", option.Trigger);
        // 手动检查是用户要做的事，失败算错误；启动时的自动检查失败不影响使用，只算警告。
        var failureLevel = option.Trigger == UpdateTrigger.Manual ? LogLevel.Error : LogLevel.Warning;

        try
        {
            var latestRelease = await GetLatestReleaseAsync();
            if (latestRelease == null)
            {
                _logger.Log(failureLevel, "检查更新失败：GitHub 和备用更新源都没有返回版本信息");
                return UpdateCheckResult.Failed("无法获取最新版本信息，请稍后再试。");
            }

            if (!TryParseReleaseVersion(latestRelease.TagName, out var latestVersion))
            {
                _logger.Log(failureLevel, "检查更新失败：最新版本号 {TagName} 的格式无法识别", latestRelease.TagName);
                return UpdateCheckResult.Failed($"最新版本号格式无法识别：{latestRelease.TagName}");
            }

            var currentVersion = GetCurrentAppVersion();

            if (latestVersion <= currentVersion)
            {
                if (option.Trigger == UpdateTrigger.Manual)
                    _logger.LogInformation("检查更新：当前已是最新版本 {Version}", currentVersion);
                return UpdateCheckResult.UpToDate("当前已安装最新版本。");
            }

            _logger.LogInformation("检查更新：发现新版本 {Latest}（当前 {Current}）", latestVersion, currentVersion);
            return await HandleUpdateDialogResultAsync(latestRelease);
        }
        catch (Exception ex)
        {
            _logger.Log(failureLevel, ex, "检查更新失败");
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
        var githubRelease = await GetLatestReleaseFromGitHubAsync();
        if (githubRelease != null)
        {
            return githubRelease;
        }

        var fallbackRelease = await GetLatestReleaseFromFallbackAsync();
        if (fallbackRelease is not null)
            _logger.LogDebug("已从备用更新源获取版本 {Version}", fallbackRelease.TagName);
        return fallbackRelease;
    }

    private async Task<GitHubRelease?> GetLatestReleaseFromGitHubAsync()
    {
        try
        {
            using var response = await HttpClient.GetAsync(GitHubApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "检查更新：GitHub 请求失败（{StatusCode} {Reason}）",
                    (int)response.StatusCode,
                    response.ReasonPhrase);
                return null;
            }

            return JsonSerializer.Deserialize<GitHubRelease>(await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "检查更新：GitHub 请求失败");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "检查更新：GitHub 请求超时");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "检查更新：GitHub 返回的内容无法解析");
            return null;
        }
    }

    private async Task<GitHubRelease?> GetLatestReleaseFromFallbackAsync()
    {
        var metadataUrl = _sourceOptions.ReleaseMetadataUrl;
        if (string.IsNullOrWhiteSpace(metadataUrl))
        {
            _logger.LogDebug("没有配置备用更新源，跳过");
            return null;
        }

        try
        {
            _logger.LogDebug("从备用更新源获取版本信息：{Url}", metadataUrl);
            using var response = await HttpClient.GetAsync(metadataUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "检查更新：备用更新源请求失败（{StatusCode} {Reason}）",
                    (int)response.StatusCode,
                    response.ReasonPhrase);
                return null;
            }

            return JsonSerializer.Deserialize<GitHubRelease>(await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "检查更新：备用更新源请求失败");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "检查更新：备用更新源请求超时");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "检查更新：备用更新源返回的内容无法解析");
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
                _logger.LogWarning("找不到更新程序，改为打开下载页面：{Path}", updaterExePath);
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
            _logger.LogWarning(ex, "更新程序启动失败，改为打开下载页面");
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
            _logger.LogError(ex, "下载页面打不开，请手动前往发布页下载");
        }
    }
}
