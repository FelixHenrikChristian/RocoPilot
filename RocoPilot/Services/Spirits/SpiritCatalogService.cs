using System.Net;
using System.Net.Http.Headers;
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
    private const string ListUrl = "https://wiki.biligame.com/rocom/%E7%B2%BE%E7%81%B5%E5%9B%BE%E9%89%B4";
    private const string SourceName = "Biligame 洛克王国:手游 Wiki 精灵图鉴";
    private const string DataFileName = "spirits.json";
    private const string DefaultApplicationDataFolder = "RocoPilot/ApplicationData";
    private const int RequestDelayMilliseconds = 30;

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
    private SpiritCatalogDocument? _document;
    private IReadOnlyList<SpiritNameMatchCandidate>? _nameMatchCandidates;

    public SpiritCatalogService(
        IOptions<LocalSettingsOptions> options,
        ILogger<SpiritCatalogService> logger)
    {
        _logger = logger;
        _localDataRoot = ResolveLocalDataRoot(options.Value);
    }

    public async Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_document is not null)
            {
                return _document;
            }

            var localPath = GetLocalDataPath();
            if (File.Exists(localPath))
            {
                _document = await ReadDocumentAsync(localPath, cancellationToken);
                _nameMatchCandidates = null;
                return _document;
            }

            var bundledPath = GetBundledDataPath();
            if (File.Exists(bundledPath))
            {
                _document = await ReadDocumentAsync(bundledPath, cancellationToken);
                _nameMatchCandidates = null;
                return _document;
            }

            _document = new SpiritCatalogDocument
            {
                Source = new SpiritCatalogSource
                {
                    Name = SourceName,
                    ListUrl = ListUrl
                }
            };
            _nameMatchCandidates = null;
            return _document;
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new SpiritCatalogSyncProgress(0, 0, "正在读取图鉴列表"));
            var listMarkup = await _httpClient.GetStringAsync(ListUrl, cancellationToken);
            var states = ParseListPage(listMarkup);

            for (var index = 0; index < states.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = states[index];
                progress?.Report(new SpiritCatalogSyncProgress(index + 1, states.Count, "正在读取精灵详情"));

                var fields = ParseWikitextFields(await _httpClient.GetStringAsync(RawUrl(state.Item.PageUrl), cancellationToken));
                EnrichWithFields(state, fields);
                await ThrottleAsync(cancellationToken);
            }

            var chains = BuildChains(states);
            var avatarDirectory = Path.Combine(_localDataRoot, "Spirits", "Avatars");
            Directory.CreateDirectory(avatarDirectory);

            for (var index = 0; index < states.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SpiritCatalogSyncProgress(index + 1, states.Count, "正在同步头像"));
                await DownloadAvatarAsync(states[index].Item, avatarDirectory, cancellationToken);
                await ThrottleAsync(cancellationToken);
            }

            var spirits = states
                .Select(state => state.Item)
                .OrderBy(item => ParseId(item.Id))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var document = new SpiritCatalogDocument
            {
                Source = new SpiritCatalogSource
                {
                    Name = SourceName,
                    ListUrl = ListUrl,
                    ScrapedAt = DateTimeOffset.UtcNow
                },
                Count = CountCatalogIds(spirits),
                Spirits = spirits,
                EvolutionChains = chains
            };

            Directory.CreateDirectory(Path.GetDirectoryName(GetLocalDataPath())!);
            await File.WriteAllTextAsync(
                GetLocalDataPath(),
                JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
                cancellationToken);

            _document = document;
            _nameMatchCandidates = null;
            progress?.Report(new SpiritCatalogSyncProgress(states.Count, states.Count, "图鉴数据同步完成"));
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

    public async Task<string> MatchSpiritNameAsync(string recognizedText, CancellationToken cancellationToken = default)
    {
        var query = TextMatchingHelper.NormalizeSpiritNameForMatching(recognizedText);
        if (query.Length == 0)
        {
            return string.Empty;
        }

        var document = await LoadAsync(cancellationToken);
        var candidates = _nameMatchCandidates ??= BuildNameMatchCandidates(document);
        if (candidates.Count == 0)
        {
            return query;
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

        return bestCandidate?.Name ?? query;
    }

    public async Task<string> ResolveEvolutionRecordNameAsync(
        string spiritName,
        SpiritEvolutionRecordMode mode,
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

        var representativeName = mode == SpiritEvolutionRecordMode.Highest
            ? ResolveRepresentativeName(document, item, item.FinalId, item.FinalName)
            : ResolveRepresentativeName(document, item, item.BaseId, item.BaseName);

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

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        httpClient.DefaultRequestHeaders.Referrer = new Uri(ListUrl);
        return httpClient;
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

    private string GetLocalDataPath()
    {
        return Path.Combine(_localDataRoot, "Spirits", DataFileName);
    }

    private static string GetBundledDataPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Configuration", "Spirits", DataFileName);
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

    private static List<ScrapedSpiritState> ParseListPage(string markup)
    {
        var divs = DivSortRegex.Matches(markup).Cast<Match>().ToList();
        var states = new List<ScrapedSpiritState>();
        var listUri = new Uri(ListUrl);

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
            var bytes = await _httpClient.GetByteArrayAsync(item.AvatarUrl, cancellationToken);
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
        }

        item.AvatarPath = ToJsonPath(Path.GetRelativePath(_localDataRoot, outputPath));
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

    private static List<string> BuildAliases(SpiritCatalogItem item)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal)
        {
            item.Name,
            item.WikiName
        };

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
        return $"{id}_{hash}.png";
    }

    private static string Decode(string value)
    {
        return WebUtility.HtmlDecode(value);
    }

    private static string ToJsonPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
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
