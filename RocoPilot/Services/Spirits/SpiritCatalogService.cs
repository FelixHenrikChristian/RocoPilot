using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Core.Services;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Models.Spirits;

using Windows.Storage;

namespace RocoPilot.Services.Spirits;

public sealed class SpiritCatalogService : ISpiritCatalogService, IDisposable
{
    private const string BiligameSourceId = "biligame";
    private const string BiligameListUrl = "https://wiki.biligame.com/rocom/%E7%B2%BE%E7%81%B5%E5%9B%BE%E9%89%B4";
    private const string BiligameSourceName = "Biligame 洛克王国:手游 Wiki 精灵图鉴";
    private const string DataFileName = "spirits.json";
    private const string SourcesDirectoryName = "Sources";
    private const string BundledCatalogMarkerFileName = "bundled-spirits.marker";
    private const string DefaultApplicationDataFolder = "RocoPilot/ApplicationData";
    private const int RequestDelayMilliseconds = 30;

    private static readonly IReadOnlyList<SpiritCatalogSourceOption> SourceOptions =
    [
        new(BiligameSourceId, BiligameSourceName, BiligameListUrl)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _bundledDataRoot;
    private readonly ILogger<SpiritCatalogService> _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _localDataRoot;
    private readonly Dictionary<string, SpiritCatalogSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public SpiritCatalogService(
        IOptions<LocalSettingsOptions> options,
        ILogger<SpiritCatalogService> logger,
        ILocalSettingsService localSettingsService)
        : this(ResolveLocalDataRoot(options.Value), AppContext.BaseDirectory, logger, localSettingsService, CreateHttpClient())
    {
    }

    internal SpiritCatalogService(string localDataRoot, string bundledDataRoot,
        ILogger<SpiritCatalogService> logger, ILocalSettingsService localSettingsService, HttpClient httpClient)
    {
        _logger = logger;
        _localSettingsService = localSettingsService;
        _localDataRoot = localDataRoot;
        _bundledDataRoot = bundledDataRoot;
        _httpClient = httpClient;
    }

    public IReadOnlyList<SpiritCatalogSourceOption> GetSources()
    {
        return SourceOptions;
    }

    public async Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await LoadAsync(await ResolvePreferredSourceIdAsync(), cancellationToken);
    }

    public async Task<SpiritCatalogDocument> LoadAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        return (await LoadSnapshotAsync(sourceId, cancellationToken)).CreateDocument();
    }

    private async Task<SpiritCatalogSnapshot> LoadSnapshotAsync(string sourceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = ResolveSource(sourceId);
            if (_snapshots.TryGetValue(source.Id, out var cachedDocument))
            {
                return cachedDocument;
            }

            var bundledPath = GetBundledDataPath(source);
            if (File.Exists(bundledPath))
            {
                await ApplyBundledCatalogUpdateAsync(source, bundledPath, cancellationToken);
            }

            var localPath = GetLocalDataPath(source);
            if (File.Exists(localPath))
            {
                var document = await ReadDocumentAsync(localPath, cancellationToken);
                EnsureDocumentSource(document, source);
                return CacheDocument(source, document);
            }

            if (File.Exists(bundledPath))
            {
                var document = await ReadDocumentAsync(bundledPath, cancellationToken);
                EnsureDocumentSource(document, source);
                return CacheDocument(source, document);
            }

            var legacyDocument = await TryLoadLegacyDocumentAsync(source, cancellationToken);
            if (legacyDocument is not null)
            {
                EnsureDocumentSource(legacyDocument, source);
                return CacheDocument(source, legacyDocument);
            }

            var emptyDocument = CreateEmptyDocument(source);
            return CacheDocument(source, emptyDocument);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpiritCatalogDocument> SyncAsync(
        IProgress<SpiritCatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await SyncAsync(await ResolvePreferredSourceIdAsync(), progress, cancellationToken);
    }

    public async Task<SpiritCatalogDocument> SyncAsync(
        string sourceId,
        IProgress<SpiritCatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = ResolveSource(sourceId);
            var document = await ScrapeBiligameAsync(source, progress, cancellationToken);

            await PersistCatalogAsync(source, document, progress, cancellationToken);

            var snapshot = CacheDocument(source, document);
            progress?.Report(new SpiritCatalogSyncProgress(document.Spirits.Count, document.Spirits.Count, "图鉴数据同步完成"));
            return snapshot.CreateDocument();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "同步精灵图鉴数据失败。");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string> MatchSpiritNameAsync(string recognizedText, CancellationToken cancellationToken = default)
    {
        return MatchSpiritNameAsync(recognizedText, 0, cancellationToken);
    }

    public async Task<string> MatchSpiritNameAsync(
        string recognizedText, double minimumSimilarity, CancellationToken cancellationToken = default)
    {
        if (TextMatchingHelper.NormalizeSpiritNameForMatching(recognizedText).Length == 0) return string.Empty;
        var snapshot = await LoadSnapshotAsync(await ResolvePreferredSourceIdAsync(), cancellationToken);
        return snapshot.Index.Match(recognizedText, minimumSimilarity);
    }

    public async Task<string> ResolveEvolutionRecordNameAsync(string spiritName, CancellationToken cancellationToken = default)
    {
        if (TextMatchingHelper.NormalizeSpiritNameForMatching(spiritName).Length == 0) return string.Empty;
        var snapshot = await LoadSnapshotAsync(await ResolvePreferredSourceIdAsync(), cancellationToken);
        return snapshot.Index.ResolveEvolutionRecordName(spiritName);
    }

    private SpiritCatalogSnapshot CacheDocument(SpiritCatalogSourceOption source, SpiritCatalogDocument document)
    {
        var snapshot = new SpiritCatalogSnapshot(document);
        _snapshots[source.Id] = snapshot;
        return snapshot;
    }

    public void Dispose() => _httpClient.Dispose();

    public string? ResolveAvatarPath(string? avatarPath)
    {
        if (string.IsNullOrWhiteSpace(avatarPath))
        {
            return null;
        }

        var normalizedPath = avatarPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalizedPath) && File.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        var candidates = new[]
        {
            Path.Combine(_localDataRoot, normalizedPath),
            Path.Combine(_bundledDataRoot, normalizedPath),
            Path.Combine(_bundledDataRoot, "Configuration", "Spirits", normalizedPath)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static SpiritCatalogSourceOption ResolveSource(string? sourceId)
    {
        return SourceOptions.FirstOrDefault(source =>
                string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase))
            ?? SourceOptions[0];
    }

    private async Task<string> ResolvePreferredSourceIdAsync()
    {
        try
        {
            var sourceId = await _localSettingsService.ReadSettingAsync<string>(SettingsKeys.SpiritCatalogSourceId);
            return ResolveSource(sourceId).Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取精灵图鉴来源设置失败，已使用默认图鉴源。");
            return BiligameSourceId;
        }
    }

    private static SpiritCatalogSourceOption? ResolveDocumentSource(SpiritCatalogDocument document)
    {
        return SourceOptions.FirstOrDefault(source =>
                string.Equals(source.Id, document.Source.Id, StringComparison.OrdinalIgnoreCase))
            ?? SourceOptions.FirstOrDefault(source =>
                string.Equals(source.ListUrl, document.Source.ListUrl, StringComparison.OrdinalIgnoreCase))
            ?? SourceOptions.FirstOrDefault(source =>
                string.Equals(source.Name, document.Source.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static SpiritCatalogDocument CreateEmptyDocument(SpiritCatalogSourceOption source)
    {
        return new SpiritCatalogDocument
        {
            Source = new SpiritCatalogSource
            {
                Id = source.Id,
                Name = source.Name,
                ListUrl = source.ListUrl
            }
        };
    }

    private static void EnsureDocumentSource(
        SpiritCatalogDocument document,
        SpiritCatalogSourceOption source)
    {
        document.Source.Id = source.Id;
        if (string.IsNullOrWhiteSpace(document.Source.Name))
        {
            document.Source.Name = source.Name;
        }

        if (string.IsNullOrWhiteSpace(document.Source.ListUrl))
        {
            document.Source.ListUrl = source.ListUrl;
        }
    }

    private async Task<SpiritCatalogDocument?> TryLoadLegacyDocumentAsync(
        SpiritCatalogSourceOption source,
        CancellationToken cancellationToken)
    {
        foreach (var path in new[] { GetLegacyLocalDataPath(), GetLegacyBundledDataPath() })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var document = await ReadDocumentAsync(path, cancellationToken);
            var documentSource = ResolveDocumentSource(document);
            if (documentSource is not null
                && string.Equals(documentSource.Id, source.Id, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
    }

    private async Task<SpiritCatalogDocument> ScrapeBiligameAsync(
        SpiritCatalogSourceOption source,
        IProgress<SpiritCatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SpiritCatalogSyncProgress(0, 0, $"正在读取{source.Name}列表"));
        var listMarkup = await GetStringAsync(source.ListUrl, source, "text/html", cancellationToken);
        var states = BiligameSpiritCatalogParser.ParseListPage(listMarkup, source.ListUrl);
        if (states.Count == 0)
        {
            throw new InvalidOperationException("Biligame 图鉴列表解析结果为空，已停止同步以避免覆盖现有图鉴数据。");
        }

        var reportedCount = BiligameSpiritCatalogParser.ParseReportedCount(listMarkup);
        if (reportedCount > 0 && states.Count != reportedCount)
        {
            throw new InvalidOperationException(
                $"Biligame 图鉴列表声明 {reportedCount} 张卡片，实际解析 {states.Count} 张，已停止同步以避免覆盖现有图鉴数据。");
        }

        progress?.Report(new SpiritCatalogSyncProgress(states.Count, states.Count, "图鉴列表解析完成"));
        return BuildDocument(source, states);
    }

    internal static SpiritCatalogDocument BuildDocument(
        SpiritCatalogSourceOption source,
        List<ScrapedSpiritState> states)
    {
        var chains = BuildChains(states);
        var spirits = states
            .Select(state => state.Item)
            .OrderBy(item => SpiritCatalogParsingHelpers.ParseCatalogId(item.Id))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SpiritCatalogDocument
        {
            Source = new SpiritCatalogSource
            {
                Id = source.Id,
                Name = source.Name,
                ListUrl = source.ListUrl,
                ScrapedAt = DateTimeOffset.UtcNow
            },
            Count = CountCatalogIds(spirits),
            Spirits = spirits,
            EvolutionChains = chains
        };
    }

    private async Task PersistCatalogAsync(
        SpiritCatalogSourceOption source,
        SpiritCatalogDocument document,
        IProgress<SpiritCatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureDocumentSource(document, source);
        var avatarDirectory = GetLocalAvatarDirectory(source);
        var avatarPathPrefix = GetLocalAvatarPathPrefix(source);
        Directory.CreateDirectory(avatarDirectory);

        for (var index = 0; index < document.Spirits.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SpiritCatalogSyncProgress(index + 1, document.Spirits.Count, "正在同步头像"));
            await DownloadAvatarAsync(
                document.Spirits[index],
                avatarDirectory,
                avatarPathPrefix,
                cancellationToken);
            await DownloadShinyAvatarAsync(
                document.Spirits[index],
                avatarDirectory,
                avatarPathPrefix,
                cancellationToken);
            await ThrottleAsync(cancellationToken);
        }

        await NormalizeAvatarFilesAsync(document, avatarDirectory, avatarPathPrefix, cancellationToken);

        progress?.Report(new SpiritCatalogSyncProgress(0, 0, "正在写入图鉴数据"));
        await WriteDocumentAsync(GetLocalDataPath(source), document, cancellationToken);
        // 文档提交后，标记和旧头像清理失败不应阻止新快照发布。
        try { await UpdateBundledCatalogMarkerAsync(source, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "更新图鉴版本标记失败。"); }
        try { PruneUnreferencedAvatarFiles(document, avatarDirectory); }
        catch (Exception ex) { _logger.LogWarning(ex, "清理旧图鉴头像失败。"); }
    }

    private async Task UpdateBundledCatalogMarkerAsync(
        SpiritCatalogSourceOption source,
        CancellationToken cancellationToken)
    {
        var bundledPath = GetBundledDataPath(source);
        if (!File.Exists(bundledPath))
        {
            return;
        }

        var marker = await BuildBundledCatalogMarkerAsync(bundledPath, cancellationToken);
        var markerPath = GetBundledCatalogMarkerPath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await AtomicFileWriter.WriteAllTextAsync(markerPath, marker + Environment.NewLine, cancellationToken);
    }

    private static async Task WriteDocumentAsync(
        string path,
        SpiritCatalogDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFileWriter.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
            cancellationToken);
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return httpClient;
    }

    private async Task<string> GetStringAsync(
        string url,
        SpiritCatalogSourceOption source,
        string accept,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(source.ListUrl);
        request.Headers.Accept.ParseAdd(accept);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string ResolveLocalDataRoot(LocalSettingsOptions options)
    {
        if (RuntimeHelper.IsMSIX)
        {
            return ApplicationData.Current.LocalFolder.Path;
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, options.ApplicationDataFolder ?? DefaultApplicationDataFolder);
    }

    private static Task ThrottleAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(RequestDelayMilliseconds, cancellationToken);
    }

    private string GetLocalSourceDirectory(SpiritCatalogSourceOption source)
    {
        return Path.Combine(_localDataRoot, "Spirits", SourcesDirectoryName, source.Id);
    }

    private string GetLocalDataPath(SpiritCatalogSourceOption source)
    {
        return Path.Combine(GetLocalSourceDirectory(source), DataFileName);
    }

    private string GetLocalAvatarDirectory(SpiritCatalogSourceOption source)
    {
        return Path.Combine(GetLocalSourceDirectory(source), "Avatars");
    }

    private static string GetLocalAvatarPathPrefix(SpiritCatalogSourceOption source)
    {
        return ToJsonPath(Path.Combine("Spirits", SourcesDirectoryName, source.Id, "Avatars"));
    }

    private string GetBundledCatalogMarkerPath(SpiritCatalogSourceOption source)
    {
        return Path.Combine(GetLocalSourceDirectory(source), BundledCatalogMarkerFileName);
    }

    private string GetBundledDataPath(SpiritCatalogSourceOption source)
    {
        return Path.Combine(GetBundledSourceDirectory(_bundledDataRoot, source.Id), DataFileName);
    }

    private static string GetBundledSourceDirectory(string root, string sourceId)
    {
        return Path.Combine(root, "Configuration", "Spirits", SourcesDirectoryName, sourceId);
    }

    private string GetLegacyLocalDataPath()
    {
        return Path.Combine(_localDataRoot, "Spirits", DataFileName);
    }

    private string GetLegacyBundledDataPath()
    {
        return Path.Combine(_bundledDataRoot, "Configuration", "Spirits", DataFileName);
    }

    private async Task ApplyBundledCatalogUpdateAsync(
        SpiritCatalogSourceOption source,
        string bundledPath,
        CancellationToken cancellationToken)
    {
        var localPath = GetLocalDataPath(source);
        var markerPath = GetBundledCatalogMarkerPath(source);
        var bundledMarker = await BuildBundledCatalogMarkerAsync(bundledPath, cancellationToken);
        var currentMarker = File.Exists(markerPath)
            ? (await File.ReadAllTextAsync(markerPath, cancellationToken)).Trim()
            : string.Empty;

        if (File.Exists(localPath)
            && string.Equals(currentMarker, bundledMarker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await AtomicFileWriter.WriteAllBytesAsync(localPath, await File.ReadAllBytesAsync(bundledPath, cancellationToken), cancellationToken);
        await AtomicFileWriter.WriteAllTextAsync(markerPath, bundledMarker + Environment.NewLine, cancellationToken);
        _snapshots.Remove(source.Id);
    }

    private static async Task<string> BuildBundledCatalogMarkerAsync(
        string bundledPath,
        CancellationToken cancellationToken)
    {
        var version = GetCurrentApplicationVersion();
        await using var stream = File.OpenRead(bundledPath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"{version}|{hash}";
    }

    private static string GetCurrentApplicationVersion()
    {
        var assembly = typeof(SpiritCatalogService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informationalVersion.Trim();
    }

    private static async Task<SpiritCatalogDocument> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SpiritCatalogDocument>(stream, JsonOptions, cancellationToken)
            ?? new SpiritCatalogDocument();
        document.Spirits ??= [];
        document.EvolutionChains ??= [];
        document.Count = CountCatalogIds(document.Spirits);
        return document;
    }

    private static List<SpiritEvolutionChain> BuildChains(List<ScrapedSpiritState> states)
    {
        var chains = new List<SpiritEvolutionChain>();
        foreach (var group in states.GroupBy(state => string.IsNullOrWhiteSpace(state.Item.BaseName)
                     ? state.Item.Name
                     : state.Item.BaseName))
        {
            var members = group
                .OrderBy(ChainSortKey)
                .ToList();
            var baseState = members.FirstOrDefault(state =>
                    string.Equals(state.Item.Name, group.Key, StringComparison.Ordinal))
                ?? members[0];
            var highestRank = members.Max(state => state.StageRank);
            var highestCandidates = members
                .Where(state => state.StageRank == highestRank)
                .ToList();
            var highest = highestCandidates.FirstOrDefault(state =>
                    state.IsPrimaryForm)
                ?? highestCandidates.FirstOrDefault(state =>
                    string.Equals(state.Item.Form, "原始形态", StringComparison.Ordinal))
                ?? highestCandidates.Last();

            var chainId = $"{baseState.Item.Id}-{group.Key}";
            var chainMembers = members.Select(ToChainMember).ToList();
            var chainNames = members.Select(state => state.Item.Name).ToList();
            var highestMembers = highestCandidates.Select(ToChainMember).ToList();

            var chain = new SpiritEvolutionChain
            {
                Id = chainId,
                BaseId = baseState.Item.Id,
                BaseName = group.Key,
                HighestId = highest.Item.Id,
                HighestName = highest.Item.Name,
                HighestCandidates = highestMembers,
                Spirits = chainMembers,
                Names = chainNames
            };
            chains.Add(chain);

            foreach (var member in members)
            {
                member.Item.ChainId = chainId;
                member.Item.BaseId = baseState.Item.Id;
                member.Item.FinalId = highest.Item.Id;
                member.Item.FinalName = highest.Item.Name;
                member.Item.EvolutionChain = chainMembers;
                member.Item.EvolutionChainNames = chainNames;
            }
        }

        return chains
            .OrderBy(chain => SpiritCatalogParsingHelpers.ParseCatalogId(chain.BaseId))
            .ThenBy(chain => chain.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task DownloadAvatarAsync(
        SpiritCatalogItem item,
        string avatarDirectory,
        string avatarPathPrefix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvatarUrl))
        {
            return;
        }

        var avatarPath = await TryDownloadAvatarAsync(
            item,
            item.AvatarUrl,
            avatarDirectory,
            avatarPathPrefix,
            logFailure: false,
            cancellationToken);
        if (avatarPath.Length > 0)
        {
            item.AvatarPath = avatarPath;
            return;
        }

        item.AvatarPath = await TryDownloadAvatarAsync(
            item,
            item.AvatarUrl,
            avatarDirectory,
            avatarPathPrefix,
            logFailure: true,
            cancellationToken);
    }

    private async Task DownloadShinyAvatarAsync(
        SpiritCatalogItem item,
        string avatarDirectory,
        string avatarPathPrefix,
        CancellationToken cancellationToken)
    {
        if (!item.HasShiny || string.IsNullOrWhiteSpace(item.ShinyAvatarUrl))
        {
            item.ShinyAvatarPath = string.Empty;
            return;
        }

        var avatarPath = await TryDownloadAvatarAsync(
            item,
            item.ShinyAvatarUrl,
            avatarDirectory,
            avatarPathPrefix,
            logFailure: false,
            cancellationToken);
        item.ShinyAvatarPath = avatarPath.Length > 0
            ? avatarPath
            : await TryDownloadAvatarAsync(
                item,
                item.ShinyAvatarUrl,
                avatarDirectory,
                avatarPathPrefix,
                logFailure: true,
                cancellationToken);
    }

    private async Task<string> TryDownloadAvatarAsync(
        SpiritCatalogItem item,
        string avatarUrl,
        string avatarDirectory,
        string avatarPathPrefix,
        bool logFailure,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return string.Empty;
        }

        var fileName = BuildAvatarFileName(item.Id, item.Name, avatarUrl);
        var outputPath = Path.Combine(avatarDirectory, fileName);
        if (!File.Exists(outputPath))
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(avatarUrl, cancellationToken);
                await AtomicFileWriter.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logFailure)
                {
                    _logger.LogWarning(ex, "同步精灵头像失败：{SpiritName} {AvatarUrl}", item.Name, avatarUrl);
                }

                return string.Empty;
            }
        }

        return ToJsonPath(Path.Combine(avatarPathPrefix, fileName));
    }

    private async Task NormalizeAvatarFilesAsync(
        SpiritCatalogDocument document,
        string avatarDirectory,
        string avatarPathPrefix,
        CancellationToken cancellationToken)
    {
        var canonicalFileNamesByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.Spirits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.AvatarPath = await NormalizeAvatarFileAsync(
                item.AvatarPath,
                avatarDirectory,
                avatarPathPrefix,
                canonicalFileNamesByHash,
                cancellationToken);
            item.ShinyAvatarPath = await NormalizeAvatarFileAsync(
                item.ShinyAvatarPath,
                avatarDirectory,
                avatarPathPrefix,
                canonicalFileNamesByHash,
                cancellationToken);
        }

    }

    private static async Task<string> NormalizeAvatarFileAsync(
        string avatarPath,
        string avatarDirectory,
        string avatarPathPrefix,
        Dictionary<string, string> canonicalFileNamesByHash,
        CancellationToken cancellationToken)
    {
        var fileName = GetAvatarFileName(avatarPath);
        if (fileName.Length == 0)
        {
            return avatarPath;
        }

        var filePath = Path.Combine(avatarDirectory, fileName);
        if (!File.Exists(filePath))
        {
            return avatarPath;
        }

        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        if (canonicalFileNamesByHash.TryGetValue(hash, out var canonicalFileName))
        {
            return ToJsonPath(Path.Combine(avatarPathPrefix, canonicalFileName));
        }

        canonicalFileNamesByHash[hash] = fileName;
        return avatarPath;
    }

    private void PruneUnreferencedAvatarFiles(SpiritCatalogDocument document, string avatarDirectory)
    {
        var referencedFileNames = document.Spirits
            .SelectMany(item => new[]
            {
                GetAvatarFileName(item.AvatarPath),
                GetAvatarFileName(item.ShinyAvatarPath)
            })
            .Where(fileName => fileName.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(avatarDirectory))
        {
            var fileName = Path.GetFileName(file);
            if (referencedFileNames.Contains(fileName))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "清理未引用精灵头像失败：{AvatarPath}", file);
            }
        }
    }

    private static (int IsBase, int Id, int SourceIndex, string Name) ChainSortKey(ScrapedSpiritState state)
    {
        return (
            string.Equals(state.Item.Name, state.Item.BaseName, StringComparison.Ordinal) ? 0 : 1,
            SpiritCatalogParsingHelpers.ParseCatalogId(state.Item.Id),
            state.SourceIndex,
            state.Item.Name);
    }

    private static SpiritEvolutionChainMember ToChainMember(ScrapedSpiritState state)
    {
        return new SpiritEvolutionChainMember
        {
            Id = state.Item.Id,
            Name = state.Item.Name,
            Stage = state.Item.Stage,
            Form = state.Item.Form,
            RegionalForm = state.Item.RegionalForm
        };
    }

    private static int CountCatalogIds(IEnumerable<SpiritCatalogItem> spirits)
    {
        return spirits
            .Select(item => item.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string BuildAvatarFileName(string id, string name, string url)
    {
        var hashBytes = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{id}\n{name}\n{url}"));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..10];
        var extension = GetImageExtension(url);
        return $"{id}_{hash}{extension}";
    }

    private static string GetAvatarFileName(string? avatarPath)
    {
        return string.IsNullOrWhiteSpace(avatarPath)
            ? string.Empty
            : Path.GetFileName(avatarPath.Trim().Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetImageExtension(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
            {
                return extension;
            }
        }

        return ".png";
    }

    private static string ToJsonPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

}
