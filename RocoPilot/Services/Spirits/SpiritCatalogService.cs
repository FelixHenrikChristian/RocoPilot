using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Models.Spirits;

using Windows.Storage;

namespace RocoPilot.Services.Spirits;

public sealed class SpiritCatalogService : ISpiritCatalogService
{
    private const string BiligameSourceId = "biligame";
    private const string BiligameListUrl = "https://wiki.biligame.com/rocom/%E7%B2%BE%E7%81%B5%E5%9B%BE%E9%89%B4";
    private const string BiligameSourceName = "Biligame 洛克王国:手游 Wiki 精灵图鉴";
    private const string LcxSourceId = "lcx";
    private const string LcxListUrl = "https://wiki.lcx.cab/lk/tujian.php";
    private const string LcxSourceName = "离愁轩 洛克王国:手游 精灵图鉴";
    private const string LcxBaseUrl = "https://wiki.lcx.cab/lk/";
    private const string DataFileName = "spirits.json";
    private const string SourcesDirectoryName = "Sources";
    private const string BundledCatalogMarkerFileName = "bundled-spirits.marker";
    private const string DefaultApplicationDataFolder = "RocoPilot/ApplicationData";
    private const int RequestDelayMilliseconds = 30;

    private static readonly IReadOnlyList<SpiritCatalogSourceOption> SourceOptions =
    [
        new(BiligameSourceId, BiligameSourceName, BiligameListUrl),
        new(LcxSourceId, LcxSourceName, LcxListUrl)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private static readonly Regex DivSortRegex = new(
        "<div class=\"divsort\"(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeRegex = new(
        "([a-zA-Z0-9_-]+)=\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdRegex = new(
        ">\\s*NO\\.\\s*(?<id>\\d+)\\s*<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameRegex = new(
        "block_2\"[^>]*>\\s*<a\\s+href=\"(?<href>[^\"]+)\"\\s+title=\"(?<title>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex IconRegex = new(
        "<img\\s+alt=\"(?<alt>[^\"]*)\"\\s+src=\"(?<src>[^\"]+)\"[^>]*class=\"rocom_prop_icon\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex FallbackImageRegex = new(
        "<img\\s+alt=\"(?<alt>[^\"]*)\"\\s+src=\"(?<src>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private readonly HttpClient _httpClient = CreateHttpClient();
    private readonly ILogger<SpiritCatalogService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _localDataRoot;
    private readonly Dictionary<string, SpiritCatalogDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<SpiritNameMatchCandidate>> _nameMatchCandidatesBySource =
        new(StringComparer.OrdinalIgnoreCase);

    public SpiritCatalogService(
        IOptions<LocalSettingsOptions> options,
        ILogger<SpiritCatalogService> logger)
    {
        _logger = logger;
        _localDataRoot = ResolveLocalDataRoot(options.Value);
    }

    public IReadOnlyList<SpiritCatalogSourceOption> GetSources()
    {
        return SourceOptions;
    }

    public async Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await LoadAsync(BiligameSourceId, cancellationToken);
    }

    public async Task<SpiritCatalogDocument> LoadAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = ResolveSource(sourceId);
            if (_documents.TryGetValue(source.Id, out var cachedDocument))
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
                _documents[source.Id] = document;
                _nameMatchCandidatesBySource.Remove(source.Id);
                return document;
            }

            if (File.Exists(bundledPath))
            {
                var document = await ReadDocumentAsync(bundledPath, cancellationToken);
                EnsureDocumentSource(document, source);
                _documents[source.Id] = document;
                _nameMatchCandidatesBySource.Remove(source.Id);
                return document;
            }

            var legacyDocument = await TryLoadLegacyDocumentAsync(source, cancellationToken);
            if (legacyDocument is not null)
            {
                EnsureDocumentSource(legacyDocument, source);
                _documents[source.Id] = legacyDocument;
                _nameMatchCandidatesBySource.Remove(source.Id);
                return legacyDocument;
            }

            var emptyDocument = CreateEmptyDocument(source);
            _documents[source.Id] = emptyDocument;
            _nameMatchCandidatesBySource.Remove(source.Id);
            return emptyDocument;
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
        return await SyncAsync(BiligameSourceId, progress, cancellationToken);
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
            var document = source.Id switch
            {
                LcxSourceId => await ScrapeLcxAsync(source, progress, cancellationToken),
                _ => await ScrapeBiligameAsync(source, progress, cancellationToken)
            };

            await PersistCatalogAsync(source, document, progress, cancellationToken);

            _documents[source.Id] = document;
            _nameMatchCandidatesBySource.Remove(source.Id);
            progress?.Report(new SpiritCatalogSyncProgress(document.Spirits.Count, document.Spirits.Count, "图鉴数据同步完成"));
            return document;
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
        string recognizedText,
        double minimumSimilarity,
        CancellationToken cancellationToken = default)
    {
        var query = TextMatchingHelper.NormalizeSpiritNameForMatching(recognizedText);
        if (query.Length == 0)
        {
            return string.Empty;
        }

        var threshold = Math.Clamp(minimumSimilarity, 0, 1);
        var document = await LoadAsync(cancellationToken);
        var source = ResolveDocumentSource(document) ?? SourceOptions[0];
        if (!_nameMatchCandidatesBySource.TryGetValue(source.Id, out var candidates))
        {
            candidates = BuildNameMatchCandidates(document);
            _nameMatchCandidatesBySource[source.Id] = candidates;
        }
        if (candidates.Count == 0)
        {
            return threshold <= 0 ? query : string.Empty;
        }

        SpiritNameMatchCandidate? bestCandidate = null;
        var bestSimilarity = -1d;
        foreach (var candidate in candidates)
        {
            foreach (var searchName in candidate.SearchNames)
            {
                var similarity = TextMatchingHelper.CalculateSimilarity(query, searchName);
                if (similarity <= bestSimilarity)
                {
                    continue;
                }

                bestSimilarity = similarity;
                bestCandidate = candidate;
                if (similarity >= 1)
                {
                    return candidate.Name;
                }
            }
        }

        if (bestCandidate is null)
        {
            return threshold <= 0 ? query : string.Empty;
        }

        return threshold <= 0 || bestSimilarity >= threshold
            ? bestCandidate.Name
            : string.Empty;
    }

    public async Task<string> ResolveEvolutionRecordNameAsync(
        string spiritName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TextMatchingHelper.NormalizeSpiritNameForMatching(spiritName);
        if (normalizedName.Length == 0)
        {
            return string.Empty;
        }

        var document = await LoadAsync(cancellationToken);
        var item = FindCatalogItemByName(document, normalizedName);
        if (item is null)
        {
            return TextMatchingHelper.NormalizeSpiritNameForDisplay(spiritName);
        }

        var representativeName = ResolveRepresentativeName(document, item, item.BaseId, item.BaseName);

        return string.IsNullOrWhiteSpace(representativeName)
            ? BuildDisplayName(item)
            : representativeName;
    }

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
            Path.Combine(AppContext.BaseDirectory, normalizedPath),
            Path.Combine(AppContext.BaseDirectory, "Configuration", "Spirits", normalizedPath)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static SpiritCatalogItem? FindCatalogItemByName(SpiritCatalogDocument document, string normalizedName)
    {
        return document.Spirits.FirstOrDefault(item => IsCatalogItemNameMatch(item, normalizedName));
    }

    private static bool IsCatalogItemNameMatch(SpiritCatalogItem item, string normalizedName)
    {
        return IsNameMatch(item.Name, normalizedName)
            || IsNameMatch(item.WikiName, normalizedName)
            || IsNameMatch(BuildDisplayName(item), normalizedName)
            || item.Aliases.Any(alias => IsNameMatch(alias, normalizedName));
    }

    private static bool IsNameMatch(string? name, string normalizedName)
    {
        return string.Equals(
            TextMatchingHelper.NormalizeSpiritNameForMatching(name),
            normalizedName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRepresentativeName(
        SpiritCatalogDocument document,
        SpiritCatalogItem item,
        string representativeId,
        string fallbackName)
    {
        var representative = document.Spirits.FirstOrDefault(candidate =>
                string.Equals(candidate.ChainId, item.ChainId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Id, representativeId, StringComparison.OrdinalIgnoreCase)
                && TextMatchingHelper.AreSameSpiritName(candidate.Name, fallbackName))
            ?? document.Spirits.FirstOrDefault(candidate =>
                string.Equals(candidate.ChainId, item.ChainId, StringComparison.OrdinalIgnoreCase)
                && TextMatchingHelper.AreSameSpiritName(candidate.Name, fallbackName))
            ?? document.Spirits.FirstOrDefault(candidate =>
                string.Equals(candidate.ChainId, item.ChainId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Id, representativeId, StringComparison.OrdinalIgnoreCase))
            ?? document.Spirits.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, representativeId, StringComparison.OrdinalIgnoreCase));

        var representativeName = representative is null
            ? fallbackName
            : BuildDisplayName(representative);
        return TextMatchingHelper.NormalizeSpiritNameForDisplay(representativeName);
    }

    private static SpiritCatalogSourceOption ResolveSource(string? sourceId)
    {
        return SourceOptions.FirstOrDefault(source =>
                string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase))
            ?? SourceOptions[0];
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
        var states = ParseBiligameListPage(listMarkup, source.ListUrl);

        for (var index = 0; index < states.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = states[index];
            progress?.Report(new SpiritCatalogSyncProgress(index + 1, states.Count, "正在读取精灵详情"));

            var fields = ParseWikitextFields(await GetStringAsync(RawUrl(state.Item.PageUrl), source, "text/plain", cancellationToken));
            EnrichWithFields(state, fields);
            await ThrottleAsync(cancellationToken);
        }

        return BuildDocument(source, states);
    }

    private async Task<SpiritCatalogDocument> ScrapeLcxAsync(
        SpiritCatalogSourceOption source,
        IProgress<SpiritCatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var records = new List<LcxPokemonDto>();
        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SpiritCatalogSyncProgress(page, 0, $"正在读取{source.Name}第 {page} 页"));

            var url = $"{LcxBaseUrl}get_pokemon_data.php?page={page}&exclude_details=1&hide_not_released=0&sort=t_id&direction=asc";
            var json = await GetStringAsync(url, source, "application/json, text/javascript, */*; q=0.01", cancellationToken);
            var pageRecords = JsonSerializer.Deserialize<List<LcxPokemonDto>>(json, JsonOptions) ?? [];
            if (pageRecords.Count == 0)
            {
                break;
            }

            records.AddRange(pageRecords);
            await ThrottleAsync(cancellationToken);
        }

        var states = BuildLcxStates(records);
        return BuildDocument(source, states);
    }

    private static SpiritCatalogDocument BuildDocument(
        SpiritCatalogSourceOption source,
        List<ScrapedSpiritState> states)
    {
        var chains = BuildChains(states);
        var spirits = states
            .Select(state => state.Item)
            .OrderBy(item => ParseId(item.Id))
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
        Directory.CreateDirectory(avatarDirectory);

        for (var index = 0; index < document.Spirits.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SpiritCatalogSyncProgress(index + 1, document.Spirits.Count, "正在同步头像"));
            await DownloadAvatarAsync(
                document.Spirits[index],
                avatarDirectory,
                GetLocalAvatarPathPrefix(source),
                cancellationToken);
            await ThrottleAsync(cancellationToken);
        }

        progress?.Report(new SpiritCatalogSyncProgress(0, 0, "正在写入图鉴数据"));
        await WriteDocumentAsync(GetLocalDataPath(source), document, cancellationToken);
        await TryWriteBundledCatalogsAsync(source, document, cancellationToken);
        await UpdateBundledCatalogMarkerAsync(source, cancellationToken);
    }

    private async Task TryWriteBundledCatalogsAsync(
        SpiritCatalogSourceOption source,
        SpiritCatalogDocument document,
        CancellationToken cancellationToken)
    {
        foreach (var directory in GetBundledCatalogDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var avatarsDirectory = Path.Combine(directory, "Avatars");
                Directory.CreateDirectory(avatarsDirectory);
                CopyAvatarsToBundledDirectory(document, avatarsDirectory);

                var bundledDocument = CloneDocument(document);
                RewriteAvatarPathsForBundledCatalog(source, bundledDocument);
                await WriteDocumentAsync(Path.Combine(directory, DataFileName), bundledDocument, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "写入内置精灵图鉴数据失败：{Directory}", directory);
            }
        }
    }

    private void CopyAvatarsToBundledDirectory(
        SpiritCatalogDocument document,
        string avatarsDirectory)
    {
        foreach (var item in document.Spirits)
        {
            var sourcePath = ResolveAvatarPath(item.AvatarPath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = Path.Combine(avatarsDirectory, Path.GetFileName(sourcePath));
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
        }
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
        await File.WriteAllTextAsync(markerPath, marker + Environment.NewLine, cancellationToken);
    }

    private static IReadOnlyList<string> GetBundledCatalogDirectories(SpiritCatalogSourceOption source)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputDirectory = GetBundledSourceDirectory(AppContext.BaseDirectory, source.Id);
        if (Directory.Exists(outputDirectory))
        {
            directories.Add(outputDirectory);
        }

        foreach (var searchRoot in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var projectDirectory = FindProjectDirectory(searchRoot);
            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                directories.Add(GetBundledSourceDirectory(projectDirectory, source.Id));
            }
        }

        return directories.ToList();
    }

    private static string? FindProjectDirectory(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RocoPilot.csproj")))
            {
                return directory.FullName;
            }

            var nestedProjectPath = Path.Combine(directory.FullName, "RocoPilot", "RocoPilot.csproj");
            if (File.Exists(nestedProjectPath))
            {
                return Path.Combine(directory.FullName, "RocoPilot");
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static SpiritCatalogDocument CloneDocument(SpiritCatalogDocument document)
    {
        return JsonSerializer.Deserialize<SpiritCatalogDocument>(
                JsonSerializer.Serialize(document, JsonOptions),
                JsonOptions)
            ?? new SpiritCatalogDocument();
    }

    private static void RewriteAvatarPathsForBundledCatalog(
        SpiritCatalogSourceOption source,
        SpiritCatalogDocument document)
    {
        foreach (var item in document.Spirits)
        {
            if (string.IsNullOrWhiteSpace(item.AvatarPath))
            {
                continue;
            }

            item.AvatarPath = ToJsonPath(
                Path.Combine("Configuration", "Spirits", SourcesDirectoryName, source.Id, "Avatars", Path.GetFileName(item.AvatarPath)));
        }
    }

    private static async Task WriteDocumentAsync(
        string path,
        SpiritCatalogDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
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
        if (string.Equals(source.Id, LcxSourceId, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        }

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

    private static string GetBundledDataPath(SpiritCatalogSourceOption source)
    {
        return Path.Combine(GetBundledSourceDirectory(AppContext.BaseDirectory, source.Id), DataFileName);
    }

    private static string GetBundledSourceDirectory(string root, string sourceId)
    {
        return Path.Combine(root, "Configuration", "Spirits", SourcesDirectoryName, sourceId);
    }

    private string GetLegacyLocalDataPath()
    {
        return Path.Combine(_localDataRoot, "Spirits", DataFileName);
    }

    private static string GetLegacyBundledDataPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Configuration", "Spirits", DataFileName);
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
        File.Copy(bundledPath, localPath, overwrite: true);
        await File.WriteAllTextAsync(markerPath, bundledMarker + Environment.NewLine, cancellationToken);
        _documents.Remove(source.Id);
        _nameMatchCandidatesBySource.Remove(source.Id);
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

    private static List<ScrapedSpiritState> ParseBiligameListPage(string markup, string listUrl)
    {
        var divs = DivSortRegex.Matches(markup).Cast<Match>().ToList();
        var states = new List<ScrapedSpiritState>();
        var listUri = new Uri(listUrl);

        for (var index = 0; index < divs.Count; index++)
        {
            var start = divs[index].Index;
            var end = index + 1 < divs.Count ? divs[index + 1].Index : markup.Length;
            var block = markup[start..end];
            var attributes = ParseAttributes(divs[index].Value);

            var idMatch = IdRegex.Match(block);
            var nameMatch = NameRegex.Match(block);
            if (!idMatch.Success || !nameMatch.Success)
            {
                continue;
            }

            var imageMatch = IconRegex.Match(block);
            if (!imageMatch.Success)
            {
                imageMatch = FallbackImageRegex.Match(block);
            }

            var avatarUrl = imageMatch.Success
                ? new Uri(listUri, Decode(imageMatch.Groups["src"].Value).Trim()).ToString()
                : string.Empty;

            var item = new SpiritCatalogItem
            {
                Id = idMatch.Groups["id"].Value,
                Name = Decode(nameMatch.Groups["title"].Value).Trim(),
                PageUrl = new Uri(listUri, Decode(nameMatch.Groups["href"].Value).Trim()).ToString(),
                AvatarUrl = avatarUrl,
                OriginalImageUrl = ToOriginalImageUrl(avatarUrl)
            };

            states.Add(new ScrapedSpiritState(
                item,
                index,
                attributes.GetValueOrDefault("data-param1", string.Empty).Trim(),
                attributes.GetValueOrDefault("data-param2", string.Empty).Trim(),
                attributes.GetValueOrDefault("data-param3", string.Empty).Trim(),
                attributes.GetValueOrDefault("data-param4", string.Empty).Trim(),
                attributes.GetValueOrDefault("data-param6", string.Empty).Trim()));
        }

        return states;
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        return AttributeRegex.Matches(tag)
            .Cast<Match>()
            .ToDictionary(
                match => match.Groups[1].Value,
                match => Decode(match.Groups[2].Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseWikitextFields(string wikitext)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;

        foreach (var rawLine in wikitext.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith('|') && line.Contains('=', StringComparison.Ordinal))
            {
                var separatorIndex = line.IndexOf('=');
                currentKey = line[1..separatorIndex].Trim();
                fields[currentKey] = line[(separatorIndex + 1)..].Trim();
                continue;
            }

            if (currentKey is not null && !line.StartsWith("}}", StringComparison.Ordinal))
            {
                fields[currentKey] = $"{fields[currentKey]}{Environment.NewLine}{line}".Trim();
            }
        }

        return fields;
    }

    private static void EnrichWithFields(
        ScrapedSpiritState state,
        IReadOnlyDictionary<string, string> fields)
    {
        var item = state.Item;
        item.WikiName = GetField(fields, "精灵名称", item.Name);
        item.BaseName = GetField(fields, "精灵初阶名称", item.Name);
        item.Stage = GetField(fields, "精灵阶段", state.ListStage);
        item.Form = GetField(fields, "精灵形态", state.ListForm);
        item.RegionalForm = GetField(fields, "地区形态名称", string.Empty);
        item.HasShiny = string.Equals(GetField(fields, "是否有异色", state.ListHasShiny), "是", StringComparison.Ordinal);
        item.PrimaryAttribute = GetField(fields, "主属性", state.ListPrimaryAttribute);
        item.SecondaryAttribute = GetField(fields, "2属性", state.ListSecondaryAttribute);
        item.UpdateVersion = GetField(fields, "更新版本", string.Empty);
        item.Aliases = BuildAliases(item);
        state.StageRank = StageRank(item.Stage);
    }

    private static List<ScrapedSpiritState> BuildLcxStates(IReadOnlyList<LcxPokemonDto> records)
    {
        var validRecords = records
            .Where(record => !string.IsNullOrWhiteSpace(record.CatalogId)
                && !string.IsNullOrWhiteSpace(record.Name))
            .ToList();

        var chainMetadata = validRecords
            .GroupBy(BuildLcxChainKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => BuildLcxChainMetadata(group),
                StringComparer.OrdinalIgnoreCase);

        var shinyKeys = validRecords
            .Where(IsLcxShiny)
            .Select(BuildLcxShinyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var states = new List<ScrapedSpiritState>();
        for (var index = 0; index < validRecords.Count; index++)
        {
            var record = validRecords[index];
            var displayName = GetLcxDisplayName(record);
            var attributes = SplitLcxAttributes(record.Attributes);
            var primaryAttribute = attributes.ElementAtOrDefault(0) ?? string.Empty;
            var secondaryAttribute = attributes.ElementAtOrDefault(1) ?? string.Empty;
            var metadata = chainMetadata[BuildLcxChainKey(record)];
            var stageNumber = ParseLcxEvolutionStage(record.EvolutionStage);
            var stage = stageNumber >= metadata.MaxStage
                ? "最终形态"
                : ToStageName(stageNumber);
            var form = GetLcxForm(record);
            var regionalForm = GetLcxRegionalForm(record);
            var imageUrl = BuildLcxImageUrl(record, displayName);
            var hasShiny = IsLcxShiny(record) || shinyKeys.Contains(BuildLcxShinyKey(record));

            var item = new SpiritCatalogItem
            {
                Id = NormalizeLcxCatalogId(record.CatalogId),
                Name = displayName,
                WikiName = displayName,
                BaseName = metadata.BaseName,
                PageUrl = BuildLcxPageUrl(record),
                AvatarUrl = imageUrl,
                OriginalImageUrl = imageUrl,
                Stage = stage,
                Form = form,
                RegionalForm = regionalForm,
                HasShiny = hasShiny,
                PrimaryAttribute = primaryAttribute,
                SecondaryAttribute = secondaryAttribute,
                UpdateVersion = record.UpdatedAt?.Trim() ?? string.Empty
            };
            var additionalAliases = new List<string>();
            if (!string.IsNullOrWhiteSpace(record.Name))
            {
                additionalAliases.Add(record.Name);
            }

            if (!string.IsNullOrWhiteSpace(record.FormDisplayName))
            {
                additionalAliases.Add(record.FormDisplayName);
            }

            item.Aliases = BuildAliases(item, additionalAliases);

            states.Add(new ScrapedSpiritState(
                item,
                index,
                stage,
                primaryAttribute,
                secondaryAttribute,
                form,
                hasShiny ? "是" : string.Empty)
            {
                StageRank = StageRank(stage)
            });
        }

        return states;
    }

    private static LcxChainMetadata BuildLcxChainMetadata(IEnumerable<LcxPokemonDto> records)
    {
        var orderedRecords = records
            .OrderBy(record => ParseLcxEvolutionStage(record.EvolutionStage))
            .ThenBy(record => ParseId(NormalizeLcxCatalogId(record.CatalogId)))
            .ThenBy(record => GetLcxDisplayName(record), StringComparer.Ordinal)
            .ToList();
        var maxStage = orderedRecords.Max(record => ParseLcxEvolutionStage(record.EvolutionStage));
        return new LcxChainMetadata(GetLcxDisplayName(orderedRecords[0]), maxStage);
    }

    private static string BuildLcxChainKey(LcxPokemonDto record)
    {
        var chainGroup = string.IsNullOrWhiteSpace(record.ChainGroup)
            ? NormalizeLcxCatalogId(record.CatalogId)
            : record.ChainGroup.Trim();
        return $"{chainGroup}|{GetLcxChainFormKey(record)}";
    }

    private static string GetLcxChainFormKey(LcxPokemonDto record)
    {
        var formName = record.FormName?.Trim();
        if (!record.IsForm
            || string.IsNullOrWhiteSpace(formName)
            || string.Equals(formName, "首领形态", StringComparison.Ordinal))
        {
            return "default";
        }

        return string.Equals(formName, "异色", StringComparison.Ordinal)
            ? "shiny"
            : formName;
    }

    private static string GetLcxDisplayName(LcxPokemonDto record)
    {
        var name = record.Name?.Trim() ?? string.Empty;
        var formName = record.FormName?.Trim() ?? string.Empty;
        if (record.IsForm && !string.IsNullOrWhiteSpace(record.FormDisplayName))
        {
            return record.FormDisplayName.Trim();
        }

        if (record.IsForm
            && !string.IsNullOrWhiteSpace(formName)
            && !string.Equals(formName, "首领形态", StringComparison.Ordinal)
            && !string.Equals(formName, "异色", StringComparison.Ordinal)
            && !name.Contains(formName, StringComparison.Ordinal))
        {
            return $"{name}（{formName}）";
        }

        return name;
    }

    private static string GetLcxForm(LcxPokemonDto record)
    {
        var formName = record.FormName?.Trim();
        if (!record.IsForm || string.IsNullOrWhiteSpace(formName))
        {
            return "原始形态";
        }

        if (string.Equals(formName, "首领形态", StringComparison.Ordinal))
        {
            return "首领形态";
        }

        return string.Equals(formName, "异色", StringComparison.Ordinal)
            ? "异色形态"
            : "地区形态";
    }

    private static string GetLcxRegionalForm(LcxPokemonDto record)
    {
        var formName = record.FormName?.Trim();
        if (string.IsNullOrWhiteSpace(formName)
            || string.Equals(formName, "首领形态", StringComparison.Ordinal)
            || string.Equals(formName, "异色", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return formName;
    }

    private static bool IsLcxShiny(LcxPokemonDto record)
    {
        return string.Equals(record.FormName?.Trim(), "异色", StringComparison.Ordinal);
    }

    private static string BuildLcxShinyKey(LcxPokemonDto record)
    {
        return $"{NormalizeLcxCatalogId(record.CatalogId)}|{record.Name?.Trim()}";
    }

    private static string BuildLcxPageUrl(LcxPokemonDto record)
    {
        var url = $"{LcxBaseUrl}detail.php?name={Uri.EscapeDataString(record.Name?.Trim() ?? string.Empty)}";
        return string.IsNullOrWhiteSpace(record.FormId)
            ? url
            : $"{url}&form_id={Uri.EscapeDataString(record.FormId.Trim())}";
    }

    private static string BuildLcxImageUrl(LcxPokemonDto record, string displayName)
    {
        var imageName = record.IsForm && !string.IsNullOrWhiteSpace(record.FormImagePath)
            ? record.FormImagePath.Trim()
            : string.IsNullOrWhiteSpace(record.Name)
                ? displayName
                : record.Name.Trim();
        return $"{LcxBaseUrl}imgs/{Uri.EscapeDataString(imageName)}.webp";
    }

    private static IReadOnlyList<string> SplitLcxAttributes(string? attributes)
    {
        return (attributes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute))
            .ToList();
    }

    private static string NormalizeLcxCatalogId(string? id)
    {
        var normalized = (id ?? string.Empty).Trim();
        return int.TryParse(normalized, out var value)
            ? value.ToString("000")
            : normalized;
    }

    private static int ParseLcxEvolutionStage(string? stage)
    {
        return int.TryParse(stage, out var value) && value > 0 ? value : 1;
    }

    private static string ToStageName(int stage)
    {
        return stage switch
        {
            <= 1 => "Ⅰ阶",
            2 => "Ⅱ阶",
            3 => "Ⅲ阶",
            4 => "Ⅳ阶",
            5 => "Ⅴ阶",
            _ => $"{stage}阶"
        };
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
            .OrderBy(chain => ParseId(chain.BaseId))
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

        var fileName = BuildAvatarFileName(item.Id, item.Name, item.AvatarUrl);
        var outputPath = Path.Combine(avatarDirectory, fileName);
        if (!File.Exists(outputPath))
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(item.AvatarUrl, cancellationToken);
                await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "同步精灵头像失败：{SpiritName} {AvatarUrl}", item.Name, item.AvatarUrl);
                return;
            }
        }

        item.AvatarPath = ToJsonPath(Path.Combine(avatarPathPrefix, fileName));
    }

    private static string RawUrl(string pageUrl)
    {
        return $"{pageUrl}{(pageUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?')}action=raw";
    }

    private static string ToOriginalImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains("/thumb/", StringComparison.Ordinal))
        {
            return url;
        }

        var parts = url.Split("/thumb/", 2, StringSplitOptions.None);
        var imageParts = parts[1].Split('/');
        if (imageParts.Length < 3)
        {
            return url;
        }

        return $"{parts[0]}/{imageParts[0]}/{imageParts[1]}/{imageParts[2]}";
    }

    private static string GetField(
        IReadOnlyDictionary<string, string> fields,
        string key,
        string fallback)
    {
        return fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback.Trim();
    }

    private static List<string> BuildAliases(
        SpiritCatalogItem item,
        IEnumerable<string>? additionalAliases = null)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal)
        {
            item.Name,
            item.WikiName
        };

        if (additionalAliases is not null)
        {
            foreach (var alias in additionalAliases)
            {
                aliases.Add(alias);
            }
        }

        if (!string.IsNullOrWhiteSpace(item.RegionalForm) && !string.IsNullOrWhiteSpace(item.WikiName))
        {
            aliases.Add($"{item.WikiName}（{item.RegionalForm}）");
            aliases.Add($"{item.WikiName}({item.RegionalForm})");
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .OrderBy(alias => alias, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<SpiritNameMatchCandidate> BuildNameMatchCandidates(SpiritCatalogDocument document)
    {
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.Spirits)
        {
            var displayName = BuildDisplayName(item);
            if (displayName.Length == 0)
            {
                continue;
            }

            if (!candidates.TryGetValue(displayName, out var searchNames))
            {
                searchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidates[displayName] = searchNames;
            }

            AddSearchName(searchNames, item.Name);
            AddSearchName(searchNames, item.WikiName);
            foreach (var alias in item.Aliases)
            {
                AddSearchName(searchNames, alias);
            }

            searchNames.Add(displayName);
        }

        return candidates
            .Select(pair => new SpiritNameMatchCandidate(pair.Key, pair.Value.ToList()))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildDisplayName(SpiritCatalogItem item)
    {
        var wikiName = TextMatchingHelper.NormalizeSpiritNameForDisplay(item.WikiName);
        return wikiName.Length == 0
            ? TextMatchingHelper.NormalizeSpiritNameForDisplay(item.Name)
            : wikiName;
    }

    private static void AddSearchName(HashSet<string> searchNames, string? name)
    {
        var normalizedName = TextMatchingHelper.NormalizeSpiritNameForMatching(name);
        if (normalizedName.Length > 0)
        {
            searchNames.Add(normalizedName);
        }
    }

    private static int StageRank(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return 0;
        }

        if (stage.Contains('Ⅴ') || stage.Contains("V", StringComparison.Ordinal))
        {
            return 5;
        }

        if (stage.Contains('Ⅳ') || stage.Contains("IV", StringComparison.Ordinal))
        {
            return 4;
        }

        if (stage.Contains('Ⅲ') || stage.Contains("III", StringComparison.Ordinal))
        {
            return 3;
        }

        if (stage.Contains('Ⅱ') || stage.Contains("II", StringComparison.Ordinal))
        {
            return 2;
        }

        if (stage.Contains('Ⅰ') || stage.Contains("I", StringComparison.Ordinal))
        {
            return 1;
        }

        return stage.Contains("最终", StringComparison.Ordinal) ? 90 : 10;
    }

    private static (int IsBase, int Id, int SourceIndex, string Name) ChainSortKey(ScrapedSpiritState state)
    {
        return (
            string.Equals(state.Item.Name, state.Item.BaseName, StringComparison.Ordinal) ? 0 : 1,
            ParseId(state.Item.Id),
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

    private static int ParseId(string id)
    {
        return int.TryParse(id, out var value) ? value : int.MaxValue;
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

    private static string Decode(string value)
    {
        return WebUtility.HtmlDecode(value);
    }

    private static string ToJsonPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    private sealed record LcxChainMetadata(string BaseName, int MaxStage);

    private sealed class LcxPokemonDto
    {
        [JsonPropertyName("t_id")]
        public string? CatalogId { get; set; }

        public string? Name { get; set; }

        public string? Attributes { get; set; }

        [JsonPropertyName("chain_group")]
        public string? ChainGroup { get; set; }

        [JsonPropertyName("evolution_stage")]
        public string? EvolutionStage { get; set; }

        [JsonPropertyName("form_id")]
        public string? FormId { get; set; }

        [JsonPropertyName("form_name")]
        public string? FormName { get; set; }

        [JsonPropertyName("form_display_name")]
        public string? FormDisplayName { get; set; }

        [JsonPropertyName("is_form")]
        public bool IsForm { get; set; }

        [JsonPropertyName("form_image_path")]
        public string? FormImagePath { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }

    private sealed class ScrapedSpiritState
    {
        public ScrapedSpiritState(
            SpiritCatalogItem item,
            int sourceIndex,
            string listStage,
            string listPrimaryAttribute,
            string listSecondaryAttribute,
            string listForm,
            string listHasShiny)
        {
            Item = item;
            SourceIndex = sourceIndex;
            ListStage = listStage;
            ListPrimaryAttribute = listPrimaryAttribute;
            ListSecondaryAttribute = listSecondaryAttribute;
            ListForm = listForm;
            ListHasShiny = listHasShiny;
        }

        public SpiritCatalogItem Item { get; }

        public int SourceIndex { get; }

        public string ListStage { get; }

        public string ListPrimaryAttribute { get; }

        public string ListSecondaryAttribute { get; }

        public string ListForm { get; }

        public string ListHasShiny { get; }

        public int StageRank { get; set; }
    }

    private sealed record SpiritNameMatchCandidate(string Name, IReadOnlyList<string> SearchNames);
}
