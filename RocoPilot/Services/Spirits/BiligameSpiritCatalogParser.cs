using System.Net;
using System.Text.RegularExpressions;

using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static class BiligameSpiritCatalogParser
{
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

    public static void EnrichWithFields(
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
        item.Aliases = SpiritCatalogParsingHelpers.BuildAliases(item);
        state.StageRank = SpiritCatalogParsingHelpers.StageRank(item.Stage);
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

    private static string Decode(string value)
    {
        return WebUtility.HtmlDecode(value);
    }
}
