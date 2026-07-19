using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static class BiligameSpiritCatalogParser
{
    private static readonly Regex CardStartRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"(?=[^\"]*\\bdivsort\\b)(?=[^\"]*\\bdex-pet-card\\b)[^\"]*\")(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AttributeRegex = new(
        "(?<name>[a-zA-Z0-9_:-]+)\\s*=\\s*(?<quote>[\"'])(?<value>.*?)\\k<quote>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex KickerRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-card-kicker\\b[^\"]*\")[^>]*>\\s*NO\\.\\s*(?<id>\\d+)\\s*(?:<span\\b[^>]*>(?<stage>.*?)</span>)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NameRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-card-name\\b[^\"]*\")[^>]*>\\s*<a\\b(?<attrs>[^>]*)>(?<text>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SubtitleRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-card-subtitle\\b[^\"]*\")[^>]*>(?<text>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NormalAvatarImageRegex = new(
        "<span\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-pet-art-normal\\b[^\"]*\")[^>]*>.*?<img\\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ShinyAvatarImageRegex = new(
        "<span\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-pet-art-shiny\\b[^\"]*\")[^>]*>.*?<img\\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex FallbackAvatarImageRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-pet-art\\b[^\"]*\")[^>]*>.*?<img\\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ReportedCountRegex = new(
        "<div\\b(?=[^>]*\\bclass\\s*=\\s*\"[^\"]*\\bdex-count-note\\b[^\"]*\")[^>]*>.*?<strong\\b[^>]*>\\s*(?<count>\\d+)\\s*</strong>",
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

        var cardStarts = CardStartRegex.Matches(markup).Cast<Match>().ToList();
        var cards = new List<ParsedCard>(cardStarts.Count);
        var listUri = new Uri(listUrl);

        for (var index = 0; index < cardStarts.Count; index++)
        {
            var start = cardStarts[index].Index;
            var end = index + 1 < cardStarts.Count
                ? cardStarts[index + 1].Index
                : markup.Length;
            var block = markup[start..end];
            cards.Add(ParseCard(block, cardStarts[index], listUri, index));
        }

        ApplyChainMetadata(cards);
        return cards.Select(card => card.State).ToList();
    }

    public static int ParseReportedCount(string markup)
    {
        var match = ReportedCountRegex.Match(markup);
        return match.Success
            && int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
    }

    private static ParsedCard ParseCard(
        string block,
        Match cardStart,
        Uri listUri,
        int sourceIndex)
    {
        var cardAttributes = ParseAttributes(cardStart.Groups["attrs"].Value);
        var kickerMatch = KickerRegex.Match(block);
        var nameMatch = NameRegex.Match(block);
        var imageMatch = NormalAvatarImageRegex.Match(block);
        if (!imageMatch.Success)
        {
            imageMatch = FallbackAvatarImageRegex.Match(block);
        }

        var shinyImageMatch = ShinyAvatarImageRegex.Match(block);
        if (!kickerMatch.Success || !nameMatch.Success || !imageMatch.Success)
        {
            throw new InvalidOperationException(
                $"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片缺少序号、名称或头像。");
        }

        var nameAttributes = ParseAttributes(nameMatch.Groups["attrs"].Value);
        var imageAttributes = ParseAttributes(imageMatch.Groups["attrs"].Value);
        var shinyImageAttributes = shinyImageMatch.Success
            ? ParseAttributes(shinyImageMatch.Groups["attrs"].Value)
            : [];
        var id = NormalizeCatalogId(kickerMatch.Groups["id"].Value);
        var wikiName = NormalizeText(nameMatch.Groups["text"].Value);
        var fullName = NormalizeText(nameAttributes.GetValueOrDefault("title", wikiName));
        var href = Decode(nameAttributes.GetValueOrDefault("href", string.Empty)).Trim();
        var avatarSource = Decode(imageAttributes.GetValueOrDefault("src", string.Empty)).Trim();
        var shinyAvatarSource = Decode(shinyImageAttributes.GetValueOrDefault("src", string.Empty)).Trim();
        if (id.Length == 0 || wikiName.Length == 0 || href.Length == 0 || avatarSource.Length == 0)
        {
            throw new InvalidOperationException(
                $"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片包含空的序号、名称、链接或头像。");
        }

        var stage = ParseStage(kickerMatch, cardAttributes);
        var form = ParseForm(cardAttributes);
        var subtitle = NormalizeText(SubtitleRegex.Match(block).Groups["text"].Value);
        var variant = IsGenericSubtitle(subtitle, form) ? string.Empty : subtitle;
        var primaryAttribute = NormalizeText(cardAttributes.GetValueOrDefault("data-param2", string.Empty));
        var secondaryAttribute = NormalizeText(cardAttributes.GetValueOrDefault("data-param3", string.Empty));
        var declaresShiny = string.Equals(
            NormalizeText(cardAttributes.GetValueOrDefault("data-param6", string.Empty)),
            "是",
            StringComparison.Ordinal);
        var hasShiny = shinyAvatarSource.Length > 0;
        if (declaresShiny != hasShiny)
        {
            throw new InvalidOperationException(
                $"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片的异色标记与异色头像不一致。");
        }

        var pageUrl = new Uri(listUri, href).ToString();
        var avatarUrl = new Uri(listUri, avatarSource).ToString();
        var shinyAvatarUrl = hasShiny
            ? new Uri(listUri, shinyAvatarSource).ToString()
            : string.Empty;

        var item = new SpiritCatalogItem
        {
            Id = id,
            Name = fullName.Length > 0 ? fullName : wikiName,
            WikiName = wikiName,
            PageUrl = pageUrl,
            AvatarUrl = avatarUrl,
            OriginalImageUrl = ToOriginalImageUrl(avatarUrl),
            ShinyAvatarUrl = shinyAvatarUrl,
            ShinyOriginalImageUrl = ToOriginalImageUrl(shinyAvatarUrl),
            Stage = stage,
            Form = form,
            RegionalForm = variant,
            HasShiny = hasShiny,
            PrimaryAttribute = primaryAttribute,
            SecondaryAttribute = secondaryAttribute
        };
        var state = new ScrapedSpiritState(item, sourceIndex)
        {
            IsPrimaryForm = string.Equals(
                NormalizeText(cardAttributes.GetValueOrDefault("data-param5", string.Empty)),
                "主形态",
                StringComparison.Ordinal)
        };

        return new ParsedCard(state);
    }

    private static void ApplyChainMetadata(IReadOnlyList<ParsedCard> cards)
    {
        var metadataByCatalogId = new Dictionary<string, ChainMetadata>(StringComparer.OrdinalIgnoreCase);
        string currentBaseName = string.Empty;

        foreach (var card in cards.Where(card => card.State.IsPrimaryForm))
        {
            var item = card.State.Item;
            var stageRank = SpiritCatalogParsingHelpers.StageRank(item.Stage);
            if (stageRank <= 1 || currentBaseName.Length == 0)
            {
                currentBaseName = item.WikiName;
            }

            if (!metadataByCatalogId.TryAdd(
                    item.Id,
                    new ChainMetadata(currentBaseName, stageRank)))
            {
                throw new InvalidOperationException(
                    $"Biligame 图鉴编号 {item.Id} 存在多个主形态卡片。");
            }
        }

        foreach (var card in cards)
        {
            var state = card.State;
            var item = state.Item;
            if (!metadataByCatalogId.TryGetValue(item.Id, out var metadata))
            {
                throw new InvalidOperationException(
                    $"Biligame 图鉴编号 {item.Id} 缺少主形态卡片。");
            }

            item.BaseName = metadata.BaseName;
            item.Aliases = SpiritCatalogParsingHelpers.BuildAliases(item);
            state.StageRank = metadata.StageRank;
        }
    }

    private static string ParseStage(
        Match kickerMatch,
        IReadOnlyDictionary<string, string> cardAttributes)
    {
        var stage = NormalizeText(kickerMatch.Groups["stage"].Value);
        if (stage.Length == 0)
        {
            stage = NormalizeText(cardAttributes.GetValueOrDefault("data-param1", string.Empty));
        }

        var form = NormalizeText(cardAttributes.GetValueOrDefault("data-param4", string.Empty));
        if (stage.Length == 0 && form.Contains("首领", StringComparison.Ordinal))
        {
            stage = "首领";
        }

        return SpiritCatalogParsingHelpers.NormalizeStage(stage);
    }

    private static string ParseForm(IReadOnlyDictionary<string, string> cardAttributes)
    {
        var form = NormalizeText(cardAttributes.GetValueOrDefault("data-param4", string.Empty));
        return form.Length > 0 ? form : "原始形态";
    }

    private static bool IsGenericSubtitle(string subtitle, string form)
    {
        return subtitle.Length == 0
            || string.Equals(subtitle, form, StringComparison.Ordinal)
            || subtitle is "原始形态" or "地区形态" or "首领形态" or "异色形态" or "主形态";
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

    private sealed record ParsedCard(ScrapedSpiritState State);

    private sealed record ChainMetadata(string BaseName, int StageRank);
}
