using System.Text.RegularExpressions;

using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static partial class BiligameSpiritCatalogParser
{
    private static readonly Regex NrcCardStartRegex = NrcElementRegex("div", "npc-card", string.Empty);
    private static readonly Regex NrcLinkRegex = NrcElementRegex("span", "npc-card-target", """\s*<a\b(?<linkAttrs>[^>]*)>""");
    private static readonly Regex NrcNameRegex = NrcElementRegex("div", "npc-name", """(?<text>.*?)</div>""");
    private static readonly Regex NrcStageRegex = NrcElementRegex("div", "npc-stage", """(?<text>.*?)</div>""");
    private static readonly Regex NrcFormRegex = NrcElementRegex("div", "npc-form", """(?<text>.*?)</div>""");
    private static readonly Regex NrcNormalImageRegex = NrcElementRegex("div", "npc-art-normal", """\s*<img\b(?<imageAttrs>[^>]*)>""");
    private static readonly Regex NrcShinyImageRegex = NrcElementRegex("div", "npc-art-shiny", """\s*<img\b(?<imageAttrs>[^>]*)>""");
    private static readonly Regex NrcReportedCountRegex = NrcElementRegex("span", "npc-total-number", """\s*(?<count>\d+)\s*</span>""");

    // class 必须是完整词，避免把 npc-card-face 等子元素当作另一张卡片。
    private static Regex NrcElementRegex(string tag, string className, string contentPattern) => new(
        $"""<{tag}\b(?=[^>]*\bclass\s*=\s*["'](?:[^"']*\s)?{className}(?:\s[^"']*)?["'])(?<attrs>[^>]*)>{contentPattern}""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static ParsedCard ParseNrcCard(string block, Match cardStart, Uri listUri, int sourceIndex)
    {
        var attributes = ParseAttributes(cardStart.Groups["attrs"].Value);
        var link = NrcLinkRegex.Match(block);
        var name = NrcNameRegex.Match(block);
        var normalImage = NrcNormalImageRegex.Match(block);
        var shinyImage = NrcShinyImageRegex.Match(block);
        var number = attributes.GetValueOrDefault("data-number", string.Empty);
        if (!int.TryParse(number, out var catalogNumber) || catalogNumber <= 0
            || !link.Success || !name.Success || !normalImage.Success)
        {
            throw new InvalidOperationException($"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片缺少有效序号、名称或头像。");
        }

        // data-id 是站内形态 ID；统计与变种分组仍使用游戏图鉴编号 data-number。
        var id = NormalizeCatalogId(number);
        var linkAttributes = ParseAttributes(link.Groups["linkAttrs"].Value);
        var wikiName = NormalizeText(name.Groups["text"].Value);
        var fullName = NormalizeText(linkAttributes.GetValueOrDefault("title", wikiName));
        var href = linkAttributes.GetValueOrDefault("href", string.Empty).Trim();
        var avatarSource = ParseAttributes(normalImage.Groups["imageAttrs"].Value).GetValueOrDefault("src", string.Empty).Trim();
        var shinySource = ParseAttributes(shinyImage.Groups["imageAttrs"].Value).GetValueOrDefault("src", string.Empty).Trim();
        if (wikiName.Length == 0 || href.Length == 0 || avatarSource.Length == 0)
        {
            throw new InvalidOperationException($"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片包含空的名称、链接或头像。");
        }

        var hasShiny = shinySource.Length > 0;
        if (string.Equals(attributes.GetValueOrDefault("data-shiny"), "yes", StringComparison.OrdinalIgnoreCase) != hasShiny)
        {
            throw new InvalidOperationException($"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片的异色标记与异色头像不一致。");
        }

        var formFlags = attributes.GetValueOrDefault("data-form", string.Empty)
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var isPrimary = formFlags.Contains("main", StringComparer.OrdinalIgnoreCase);
        var isLord = formFlags.Contains("lord", StringComparer.OrdinalIgnoreCase);
        var isRegional = formFlags.Contains("regional", StringComparer.OrdinalIgnoreCase);
        var stage = SpiritCatalogParsingHelpers.NormalizeStage(NormalizeText(NrcStageRegex.Match(block).Groups["text"].Value));
        if ((!isPrimary && !isLord && !isRegional) || stage.Length == 0)
        {
            throw new InvalidOperationException($"Biligame 图鉴列表第 {sourceIndex + 1} 张卡片缺少形态或阶数。");
        }

        var form = isLord ? "首领形态" : isRegional ? "地区形态" : "原始形态";
        var subtitle = NormalizeText(NrcFormRegex.Match(block).Groups["text"].Value);
        var types = attributes.GetValueOrDefault("data-type", string.Empty)
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var avatarUrl = new Uri(listUri, avatarSource).ToString();
        var shinyUrl = hasShiny ? new Uri(listUri, shinySource).ToString() : string.Empty;
        var item = new SpiritCatalogItem
        {
            Id = id,
            Name = fullName.Length > 0 ? fullName : wikiName,
            WikiName = wikiName,
            PageUrl = new Uri(listUri, href).ToString(),
            AvatarUrl = avatarUrl,
            OriginalImageUrl = ToOriginalImageUrl(avatarUrl),
            ShinyAvatarUrl = shinyUrl,
            ShinyOriginalImageUrl = ToOriginalImageUrl(shinyUrl),
            Stage = stage,
            Form = form,
            RegionalForm = IsGenericSubtitle(subtitle, form) ? string.Empty : subtitle,
            HasShiny = hasShiny,
            PrimaryAttribute = types.ElementAtOrDefault(0) ?? string.Empty,
            SecondaryAttribute = types.ElementAtOrDefault(1) ?? string.Empty
        };
        return new ParsedCard(new ScrapedSpiritState(item, sourceIndex) { IsPrimaryForm = isPrimary });
    }
}
