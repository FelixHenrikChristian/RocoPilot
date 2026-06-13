using System.Net;
using System.Text.RegularExpressions;

using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static class BiligameSpiritCatalogParser
{
    private static readonly Regex DivSortRegex = new(
        "<div\\s+class=\"(?=[^\"]*\\bdivsort\\b)(?=[^\"]*\\bdex-pet-card\\b)[^\"]*\"(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeRegex = new(
        "([a-zA-Z0-9_-]+)=\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdRegex = new(
        ">\\s*NO\\.\\s*(?<id>\\d+)\\s*<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameRegex = new(
        "dex-card-name[^>]*>\\s*<a\\s+href=\"(?<href>[^\"]+)\"\\s+title=\"(?<title>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex AvatarImageRegex = new(
        "<div\\s+class=\"[^\"]*\\bdex-pet-art\\b[^\"]*\"[^>]*>.*?<img\\s+alt=\"(?<alt>[^\"]*)\"\\s+src=\"(?<src>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex EvolutionNameRegex = new(
        "sprite-evolve-btn\"[^>]*\\bdata-link=\"(?<name>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static List<ScrapedSpiritState> ParseListPage(string markup, string listUrl)
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

            var imageMatch = AvatarImageRegex.Match(block);

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

    public static string BuildRawPageUrl(string pageUrl)
    {
        return $"{pageUrl}{(pageUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?')}action=raw";
    }

    public static Dictionary<string, string> ParseWikitextFields(string wikitext)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;

        foreach (var rawLine in wikitext.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var fieldStartIndex = GetFieldStartIndex(line);
            var separatorIndex = fieldStartIndex >= 0
                ? line.IndexOf('=', fieldStartIndex)
                : -1;
            if (separatorIndex >= fieldStartIndex && fieldStartIndex >= 0)
            {
                currentKey = line[fieldStartIndex..separatorIndex].Trim();
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

    public static bool HasRequiredWikitextFields(IReadOnlyDictionary<string, string> fields)
    {
        return HasField(fields, "精灵名称")
            && HasField(fields, "精灵初阶名称")
            && HasField(fields, "精灵阶段");
    }

    public static bool IsStubWikitext(string wikitext)
    {
        return string.Equals(wikitext.Trim(), "{{精灵图鉴}}", StringComparison.Ordinal);
    }

    public static bool IsInvalidWikitextResponse(string wikitext)
    {
        return string.IsNullOrWhiteSpace(wikitext)
            || wikitext.Contains("Frequency Capped", StringComparison.OrdinalIgnoreCase)
            || wikitext.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnrichWithFields(
        ScrapedSpiritState state,
        IReadOnlyDictionary<string, string> fields)
    {
        var item = state.Item;
        item.WikiName = GetField(fields, "精灵名称", item.Name);
        item.BaseName = GetField(fields, "精灵初阶名称", item.Name);
        item.Stage = SpiritCatalogParsingHelpers.NormalizeStage(GetField(fields, "精灵阶段", GetListStageFallback(state)));
        item.Form = PreferListField(state.ListForm, fields, "精灵形态");
        item.RegionalForm = GetField(fields, "地区形态名称", string.Empty);
        item.HasShiny = string.Equals(
            PreferListField(state.ListHasShiny, fields, "是否有异色"),
            "是",
            StringComparison.Ordinal);
        item.PrimaryAttribute = PreferListField(state.ListPrimaryAttribute, fields, "主属性");
        item.SecondaryAttribute = PreferListField(state.ListSecondaryAttribute, fields, "2属性");
        item.UpdateVersion = GetField(fields, "更新版本", string.Empty);
        item.Aliases = SpiritCatalogParsingHelpers.BuildAliases(item);
        state.StageRank = SpiritCatalogParsingHelpers.StageRank(item.Stage);
    }

    public static IReadOnlyList<string> ParseEvolutionNames(string markup)
    {
        return EvolutionNameRegex.Matches(markup)
            .Cast<Match>()
            .Select(match => Decode(match.Groups["name"].Value).Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static void ApplyEvolutionNames(
        IReadOnlyList<ScrapedSpiritState> states,
        IReadOnlyDictionary<string, IReadOnlyList<string>> evolutionNamesByName)
    {
        foreach (var state in states)
        {
            var item = state.Item;
            var wikiName = !string.IsNullOrWhiteSpace(item.WikiName) ? item.WikiName.Trim() : item.Name.Trim();
            var itemName = item.Name.Trim();
            if ((!evolutionNamesByName.TryGetValue(wikiName, out var evolutionNames)
                    && !evolutionNamesByName.TryGetValue(itemName, out evolutionNames))
                || evolutionNames.Count == 0)
            {
                continue;
            }

            var evolutionIndex = IndexOf(evolutionNames, wikiName);
            if (evolutionIndex < 0)
            {
                evolutionIndex = IndexOf(evolutionNames, itemName);
            }

            if (evolutionIndex < 0)
            {
                evolutionIndex = InferEvolutionIndex(item.Stage, evolutionNames.Count);
            }

            if (evolutionIndex < 0)
            {
                continue;
            }

            item.BaseName = evolutionNames[0];
            item.Stage = ToStageName(evolutionIndex, evolutionNames.Count);
            item.Aliases = SpiritCatalogParsingHelpers.BuildAliases(item);
            state.StageRank = SpiritCatalogParsingHelpers.StageRank(item.Stage);
        }
    }

    private static int InferEvolutionIndex(string stage, int evolutionCount)
    {
        if (evolutionCount <= 0)
        {
            return -1;
        }

        var stageRank = SpiritCatalogParsingHelpers.StageRank(stage);
        if (stageRank >= 90)
        {
            return evolutionCount - 1;
        }

        return stageRank >= 1 && stageRank <= evolutionCount ? stageRank - 1 : -1;
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

    private static string PreferListField(
        string listValue,
        IReadOnlyDictionary<string, string> fields,
        string key)
    {
        return !string.IsNullOrWhiteSpace(listValue)
            ? listValue.Trim()
            : GetField(fields, key, string.Empty);
    }

    private static string GetListStageFallback(ScrapedSpiritState state)
    {
        if (!string.IsNullOrWhiteSpace(state.ListStage))
        {
            return state.ListStage.Trim();
        }

        return state.ListForm.Contains("首领", StringComparison.Ordinal)
            ? state.ListForm.Trim()
            : string.Empty;
    }

    private static bool HasField(IReadOnlyDictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ToStageName(int zeroBasedIndex, int count)
    {
        if (zeroBasedIndex >= count - 1)
        {
            return "最终形态";
        }

        return zeroBasedIndex switch
        {
            <= 0 => "Ⅰ阶",
            1 => "Ⅱ阶",
            2 => "Ⅲ阶",
            3 => "Ⅳ阶",
            4 => "Ⅴ阶",
            _ => $"{zeroBasedIndex + 1}阶"
        };
    }

    private static int GetFieldStartIndex(string line)
    {
        if (line.StartsWith('|'))
        {
            return 1;
        }

        if (!line.StartsWith("{{", StringComparison.Ordinal))
        {
            return -1;
        }

        var templateFieldStart = line.IndexOf('|');
        return templateFieldStart >= 0 ? templateFieldStart + 1 : -1;
    }

    private static string Decode(string value)
    {
        return WebUtility.HtmlDecode(value);
    }
}
