using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static class BiligameSpiritCatalogParser
{
    private static readonly Regex NrcCardStartRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-card(?:\\s|\")[^\"]*\")(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NrcTargetRegex = new(
        "<a\\b(?<attrs>[^>]*)>(?<text>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NrcNameRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-name\\b[^\"]*\")[^>]*>(?<text>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NrcStageRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-stage\\b[^\"]*\")[^>]*>(?<text>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NrcFormRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-form\\b[^\"]*\")[^>]*>(?<text>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NrcNormalImageRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-art-normal\\b[^\"]*\")[^>]*>.*?<img\\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NrcShinyImageRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-art-shiny\\b[^\"]*\")[^>]*>.*?<img\\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex AttributeRegex = new(
        "(?<name>[a-zA-Z0-9_:-]+)\\s*=\\s*(?<quote>[\"'])(?<value>.*?)\\k<quote>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex NrcReportedCountRegex = new(
        "<(?:span|div)\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bnpc-total-number\\b[^\"]*\")[^>]*>\\s*(?<count>\\d+)\\s*</(?:span|div)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex WhitespaceRegex = new(
        "\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<ScrapedSpiritState> ParseListPage(string markup, string listUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markup);
        ArgumentException.ThrowIfNullOrWhiteSpace(listUrl);

        return ParseNrcListPage(markup, listUrl);
    }

    private static List<ScrapedSpiritState> ParseNrcListPage(string markup, string listUrl)
    {
        var cardStarts = NrcCardStartRegex.Matches(markup).Cast<Match>().ToList();
        var cards = new List<ScrapedSpiritState>(cardStarts.Count);
        var listUri = new Uri(listUrl);

        for (var index = 0; index < cardStarts.Count; index++)
        {
            var start = cardStarts[index].Index;
            var end = index + 1 < cardStarts.Count
                ? cardStarts[index + 1].Index
                : markup.Length;
            var block = markup[start..end];
            cards.Add(ParseNrcCard(block, cardStarts[index], listUri, index));
        }

        ApplyNrcChainMetadata(cards);
        return cards;
    }

    private static ScrapedSpiritState ParseNrcCard(
        string block,
        Match cardStart,
        Uri listUri,
        int sourceIndex)
    {
        var cardAttributes = ParseAttributes(cardStart.Groups["attrs"].Value);
        var targetMatch = NrcTargetRegex.Match(block);
        var nameMatch = NrcNameRegex.Match(block);
        var normalImageMatch = NrcNormalImageRegex.Match(block);
        var shinyImageMatch = NrcShinyImageRegex.Match(block);
        if (!targetMatch.Success || !nameMatch.Success || !normalImageMatch.Success)
        {
            throw new InvalidOperationException(
                $"Biligame 新版图鉴第 {sourceIndex + 1} 张卡片缺少链接、名称或头像。");
        }

        var targetAttributes = ParseAttributes(targetMatch.Groups["attrs"].Value);
        var id = NormalizeCatalogId(cardAttributes.GetValueOrDefault("data-number", string.Empty));
        if (id.Length == 0)
        {
            id = NormalizeCatalogId(
                cardAttributes.GetValueOrDefault("data-id", string.Empty).Replace("pet_", string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        var wikiName = NormalizeText(nameMatch.Groups["text"].Value);
        var fullName = NormalizeText(targetAttributes.GetValueOrDefault("title", wikiName));
        var href = Decode(targetAttributes.GetValueOrDefault("href", string.Empty)).Trim();
        var avatarSource = Decode(ParseAttributes(normalImageMatch.Groups["attrs"].Value).GetValueOrDefault("src", string.Empty)).Trim();
        var shinyAvatarSource = shinyImageMatch.Success
            ? Decode(ParseAttributes(shinyImageMatch.Groups["attrs"].Value).GetValueOrDefault("src", string.Empty)).Trim()
            : string.Empty;
        if (id.Length == 0 || wikiName.Length == 0 || href.Length == 0 || avatarSource.Length == 0)
        {
            throw new InvalidOperationException(
                $"Biligame 新版图鉴第 {sourceIndex + 1} 张卡片包含空的编号、名称、链接或头像。");
        }

        var stageText = NormalizeText(NrcStageRegex.Match(block).Groups["text"].Value);
        var stage = SpiritCatalogParsingHelpers.NormalizeStage(stageText.Length > 0
            ? stageText
            : cardAttributes.GetValueOrDefault("data-stage", string.Empty));
        var form = NormalizeText(NrcFormRegex.Match(block).Groups["text"].Value);
        if (form.Length == 0)
        {
            form = cardAttributes.GetValueOrDefault("data-form", string.Empty) switch
            {
                "lord" => "首领形态",
                "main" => "原始形态",
                _ => "原始形态"
            };
        }

        var typeValues = cardAttributes.GetValueOrDefault("data-type", string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var item = new SpiritCatalogItem
        {
            Id = id,
            Name = fullName.Length > 0 ? fullName : wikiName,
            WikiName = wikiName,
            PageUrl = new Uri(listUri, href).ToString(),
            AvatarUrl = new Uri(listUri, avatarSource).ToString(),
            OriginalImageUrl = ToOriginalImageUrl(new Uri(listUri, avatarSource).ToString()),
            ShinyAvatarUrl = shinyAvatarSource.Length > 0 ? new Uri(listUri, shinyAvatarSource).ToString() : string.Empty,
            ShinyOriginalImageUrl = shinyAvatarSource.Length > 0 ? ToOriginalImageUrl(new Uri(listUri, shinyAvatarSource).ToString()) : string.Empty,
            Stage = stage,
            Form = form,
            HasShiny = string.Equals(cardAttributes.GetValueOrDefault("data-shiny", string.Empty), "yes", StringComparison.OrdinalIgnoreCase),
            PrimaryAttribute = typeValues.ElementAtOrDefault(0) ?? string.Empty,
            SecondaryAttribute = typeValues.ElementAtOrDefault(1) ?? string.Empty
        };
        if (item.HasShiny != (shinyAvatarSource.Length > 0))
        {
            throw new InvalidOperationException(
                $"Biligame 新版图鉴第 {sourceIndex + 1} 张卡片的异色标记与异色头像不一致。");
        }

        var isPrimary = string.Equals(cardAttributes.GetValueOrDefault("data-form", string.Empty), "main", StringComparison.OrdinalIgnoreCase);
        return new ScrapedSpiritState(item, sourceIndex)
        {
            IsPrimaryForm = isPrimary,
            IsChainStart = cardAttributes.GetValueOrDefault("data-position", string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => string.Equals(value, "initial", StringComparison.OrdinalIgnoreCase))
        };
    }

    public static int ParseReportedCount(string markup)
    {
        var match = NrcReportedCountRegex.Match(markup);
        return match.Success
            && int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
    }

    private static void ApplyNrcChainMetadata(IReadOnlyList<ScrapedSpiritState> cards)
    {
        var currentBaseName = string.Empty;
        foreach (var state in cards)
        {
            var item = state.Item;
            var stageRank = SpiritCatalogParsingHelpers.StageRank(item.Stage);
            if (state.IsChainStart || (state.IsPrimaryForm && stageRank <= 1) || currentBaseName.Length == 0)
            {
                currentBaseName = item.WikiName;
            }

            if (currentBaseName.Length == 0)
            {
                currentBaseName = item.WikiName;
            }

            item.BaseName = currentBaseName;
            item.Aliases = SpiritCatalogParsingHelpers.BuildAliases(item);
            state.StageRank = stageRank;
        }
    }


    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        return AttributeRegex.Matches(tag)
            .Cast<Match>()
            .ToDictionary(
                match => match.Groups["name"].Value,
                match => Decode(match.Groups["value"].Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeCatalogId(string id)
    {
        var normalized = NormalizeText(id);
        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("000", CultureInfo.InvariantCulture)
            : normalized;
    }

    private static string NormalizeText(string? value)
    {
        var decoded = Decode(HtmlTagRegex.Replace(value ?? string.Empty, " "))
            .Replace('\u00A0', ' ');
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static string ToOriginalImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains("/thumb/", StringComparison.Ordinal))
        {
            return url;
        }

        var parts = url.Split("/thumb/", 2, StringSplitOptions.None);
        var imageParts = parts[1].Split('/');
        return imageParts.Length < 3
            ? url
            : $"{parts[0]}/{imageParts[0]}/{imageParts[1]}/{imageParts[2]}";
    }

    private static string Decode(string value)
    {
        return WebUtility.HtmlDecode(value);
    }

}
