using System.Net;
using System.Text.RegularExpressions;

using RocoPilot.Helpers;
using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static class LcxSpiritCatalogParser
{
    private static readonly Regex ShinySuffixRegex = new(
        "异色(?:S\\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DetailAvatarRegex = new(
        "<img\\b(?=[^>]*\\bid=\"pokemon-display-image\")[^>]*\\bsrc=\"(?<src>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static List<ScrapedSpiritState> BuildStates(
        IReadOnlyList<LcxPokemonDto> records,
        string baseUrl)
    {
        var validRecords = records
            .Where(record => !string.IsNullOrWhiteSpace(record.CatalogId)
                && !string.IsNullOrWhiteSpace(record.Name))
            .ToList();

        var baseNamesByCatalogId = BuildCatalogBaseNames(validRecords);
        var chainMetadata = validRecords
            .GroupBy(BuildChainKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => BuildChainMetadata(group, baseNamesByCatalogId),
                StringComparer.OrdinalIgnoreCase);

        var shinyKeys = validRecords
            .Where(IsShiny)
            .Select(BuildShinyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var states = new List<ScrapedSpiritState>();
        for (var index = 0; index < validRecords.Count; index++)
        {
            var record = validRecords[index];
            var nameParts = BuildNameParts(record, baseNamesByCatalogId);
            var displayName = nameParts.DisplayName;
            var attributes = SplitAttributes(record.Attributes);
            var primaryAttribute = attributes.ElementAtOrDefault(0) ?? string.Empty;
            var secondaryAttribute = attributes.ElementAtOrDefault(1) ?? string.Empty;
            var metadata = chainMetadata[BuildChainKey(record)];
            var stageNumber = ParseEvolutionStage(record.EvolutionStage);
            var stage = stageNumber >= metadata.MaxStage
                ? "最终形态"
                : ToStageName(stageNumber);
            var form = GetForm(record);
            var regionalForm = GetRegionalForm(record, nameParts);
            var imageUrl = BuildImageUrl(record, displayName, baseUrl);
            var hasShiny = IsShiny(record) || shinyKeys.Contains(BuildShinyKey(record));

            var item = new SpiritCatalogItem
            {
                Id = NormalizeCatalogId(record.CatalogId),
                Name = displayName,
                WikiName = displayName,
                BaseName = metadata.BaseName,
                PageUrl = BuildPageUrl(record, baseUrl),
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

            item.Aliases = SpiritCatalogParsingHelpers.BuildAliases(item, BuildAdditionalAliases(record, item));

            states.Add(new ScrapedSpiritState(
                item,
                index)
            {
                StageRank = SpiritCatalogParsingHelpers.StageRank(stage)
            });
        }

        return states;
    }

    public static string ParseDetailAvatarUrl(string markup, string baseUrl)
    {
        var match = DetailAvatarRegex.Match(markup);
        return match.Success
            ? new Uri(new Uri(baseUrl), WebUtility.HtmlDecode(match.Groups["src"].Value).Trim()).ToString()
            : string.Empty;
    }

    private static IEnumerable<string> BuildAdditionalAliases(LcxPokemonDto record, SpiritCatalogItem item)
    {
        var rawName = TextMatchingHelper.NormalizeSpiritNameInput(record.Name);
        var itemName = TextMatchingHelper.NormalizeSpiritNameInput(item.Name);
        var baseName = TextMatchingHelper.NormalizeSpiritNameInput(item.BaseName);
        if (rawName.Length > 0
            && (!record.IsForm
                || IsLeaderForm(record)
                || string.Equals(rawName, itemName, StringComparison.Ordinal)
                || string.Equals(rawName, baseName, StringComparison.Ordinal)))
        {
            yield return rawName;
        }

        var formDisplayName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormDisplayName);
        if (formDisplayName.Length > 0)
        {
            yield return formDisplayName;
        }
    }

    private static ChainMetadata BuildChainMetadata(
        IEnumerable<LcxPokemonDto> records,
        IReadOnlyDictionary<string, string> baseNamesByCatalogId)
    {
        var orderedRecords = records
            .OrderBy(record => ParseEvolutionStage(record.EvolutionStage))
            .ThenBy(record => SpiritCatalogParsingHelpers.ParseCatalogId(NormalizeCatalogId(record.CatalogId)))
            .ThenBy(record => BuildNameParts(record, baseNamesByCatalogId).DisplayName, StringComparer.Ordinal)
            .ToList();
        var maxStage = orderedRecords.Max(record => ParseEvolutionStage(record.EvolutionStage));
        return new ChainMetadata(BuildNameParts(orderedRecords[0], baseNamesByCatalogId).BaseName, maxStage);
    }

    private static string BuildChainKey(LcxPokemonDto record)
    {
        return string.IsNullOrWhiteSpace(record.ChainGroup)
            ? NormalizeCatalogId(record.CatalogId)
            : record.ChainGroup.Trim();
    }

    private static Dictionary<string, string> BuildCatalogBaseNames(IReadOnlyList<LcxPokemonDto> records)
    {
        var baseNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in records.GroupBy(record => NormalizeCatalogId(record.CatalogId), StringComparer.OrdinalIgnoreCase))
        {
            var candidates = new List<(string Name, int Rank)>();
            foreach (var record in group)
            {
                var name = TextMatchingHelper.NormalizeSpiritNameInput(record.Name);
                var formName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormName);
                var formDisplayName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormDisplayName);

                if (record.IsForm
                    && !IsLeaderForm(record)
                    && !IsShiny(record)
                    && formName.Length > 0
                    && formDisplayName.EndsWith(formName, StringComparison.Ordinal))
                {
                    AddBaseNameCandidate(candidates, formDisplayName[..^formName.Length], 0);
                }

                if (record.IsForm && IsShiny(record))
                {
                    AddBaseNameCandidate(candidates, name, 0);
                    AddBaseNameCandidate(candidates, StripShinySuffix(formDisplayName), 1);
                }

                if (name.Length > 0 && !LooksLikeVariantName(name))
                {
                    AddBaseNameCandidate(candidates, name, record.IsForm ? 2 : 0);
                }
                else
                {
                    AddBaseNameCandidate(candidates, name, 10);
                }
            }

            var baseName = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
                .OrderBy(candidate => candidate.Rank)
                .ThenBy(candidate => candidate.Name.Length)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Select(candidate => candidate.Name)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                baseNames[group.Key] = baseName;
            }
        }

        return baseNames;
    }

    private static void AddBaseNameCandidate(List<(string Name, int Rank)> candidates, string? name, int rank)
    {
        var normalizedName = TextMatchingHelper.NormalizeSpiritNameInput(name);
        if (normalizedName.Length > 0)
        {
            candidates.Add((normalizedName, rank));
        }
    }

    private static NameParts BuildNameParts(
        LcxPokemonDto record,
        IReadOnlyDictionary<string, string> baseNamesByCatalogId)
    {
        var catalogId = NormalizeCatalogId(record.CatalogId);
        var name = TextMatchingHelper.NormalizeSpiritNameInput(record.Name);
        var formName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormName);
        var formDisplayName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormDisplayName);
        var baseName = baseNamesByCatalogId.TryGetValue(catalogId, out var inferredBaseName) && inferredBaseName.Length > 0
            ? inferredBaseName
            : name;

        if (IsLeaderForm(record))
        {
            return new NameParts(
                baseName,
                formDisplayName.Length > 0 ? formDisplayName : name,
                string.Empty);
        }

        if (IsShiny(record))
        {
            var variantName = ExtractVariantName(formDisplayName, baseName);
            if (variantName.Length == 0)
            {
                variantName = formDisplayName.Length > 0 ? formDisplayName : "异色";
            }

            return new NameParts(baseName, FormatVariantDisplayName(baseName, variantName), variantName);
        }

        if (record.IsForm && formName.Length > 0)
        {
            var variantName = ExtractVariantName(formDisplayName, baseName);
            if (variantName.Length == 0)
            {
                variantName = formName;
            }

            return new NameParts(baseName, FormatVariantDisplayName(baseName, variantName), variantName);
        }

        var defaultVariantName = ExtractVariantName(name, baseName);
        return defaultVariantName.Length == 0
            ? new NameParts(baseName, name, string.Empty)
            : new NameParts(baseName, FormatVariantDisplayName(baseName, defaultVariantName), defaultVariantName);
    }

    private static string GetForm(LcxPokemonDto record)
    {
        var formName = record.FormName?.Trim();
        if (!record.IsForm || string.IsNullOrWhiteSpace(formName))
        {
            return "原始形态";
        }

        if (IsLeaderForm(record))
        {
            return "首领形态";
        }

        return IsShiny(record)
            ? "异色形态"
            : "地区形态";
    }

    private static string GetRegionalForm(LcxPokemonDto record, NameParts nameParts)
    {
        var formName = record.FormName?.Trim();
        if (IsLeaderForm(record))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(nameParts.VariantName))
        {
            return nameParts.VariantName;
        }

        return string.IsNullOrWhiteSpace(formName) || string.Equals(formName, "异色", StringComparison.Ordinal)
            ? string.Empty
            : formName;
    }

    private static bool IsLeaderForm(LcxPokemonDto record)
    {
        return string.Equals(record.FormType?.Trim(), "boss", StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.FormName?.Trim(), "首领形态", StringComparison.Ordinal);
    }

    private static bool IsShiny(LcxPokemonDto record)
    {
        return string.Equals(record.FormType?.Trim(), "color", StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.FormName?.Trim(), "异色", StringComparison.Ordinal)
            || ShinySuffixRegex.IsMatch(TextMatchingHelper.NormalizeSpiritNameInput(record.FormName));
    }

    private static string BuildShinyKey(LcxPokemonDto record)
    {
        return $"{NormalizeCatalogId(record.CatalogId)}|{record.Name?.Trim()}";
    }

    private static string ExtractVariantName(string fullName, string baseName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(baseName))
        {
            return string.Empty;
        }

        var normalizedFullName = TextMatchingHelper.NormalizeSpiritNameInput(fullName);
        var normalizedBaseName = TextMatchingHelper.NormalizeSpiritNameInput(baseName);
        if (normalizedFullName.Length <= normalizedBaseName.Length
            || !normalizedFullName.StartsWith(normalizedBaseName, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return TrimVariantBrackets(normalizedFullName[normalizedBaseName.Length..].Trim());
    }

    private static string FormatVariantDisplayName(string baseName, string variantName)
    {
        return string.IsNullOrWhiteSpace(variantName)
            ? baseName
            : $"{baseName}（{TrimVariantBrackets(variantName)}）";
    }

    private static string TrimVariantBrackets(string variantName)
    {
        var trimmed = variantName.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '（' && trimmed[^1] == '）')
                || (trimmed[0] == '(' && trimmed[^1] == ')')))
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool LooksLikeVariantName(string name)
    {
        var normalizedName = TextMatchingHelper.NormalizeSpiritNameInput(name);
        return normalizedName.EndsWith("的样子", StringComparison.Ordinal)
            || ShinySuffixRegex.IsMatch(normalizedName);
    }

    private static string StripShinySuffix(string name)
    {
        var normalizedName = TextMatchingHelper.NormalizeSpiritNameInput(name);
        return ShinySuffixRegex.Replace(normalizedName, string.Empty).Trim();
    }

    private static string BuildPageUrl(LcxPokemonDto record, string baseUrl)
    {
        var url = $"{baseUrl}detail.php?name={Uri.EscapeDataString(record.Name?.Trim() ?? string.Empty)}";
        return string.IsNullOrWhiteSpace(record.FormId)
            ? url
            : $"{url}&form_id={Uri.EscapeDataString(record.FormId.Trim())}";
    }

    private static string BuildImageUrl(LcxPokemonDto record, string displayName, string baseUrl)
    {
        var imageName = BuildImageName(record, displayName);
        return $"{baseUrl}imgs/{Uri.EscapeDataString(imageName)}.webp";
    }

    private static string BuildImageName(LcxPokemonDto record, string displayName)
    {
        var name = TextMatchingHelper.NormalizeSpiritNameInput(record.Name);
        if (!record.IsForm)
        {
            return name.Length > 0 ? name : displayName;
        }

        var imageName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormImagePath);
        if (imageName.Length == 0)
        {
            imageName = TextMatchingHelper.NormalizeSpiritNameInput(record.FormDisplayName);
        }

        if (imageName.Length == 0)
        {
            return name.Length > 0 ? name : displayName;
        }

        if (!IsLeaderForm(record)
            && name.Length > 0
            && !imageName.StartsWith(name, StringComparison.Ordinal))
        {
            return $"{name}{imageName}";
        }

        return imageName;
    }

    private static IReadOnlyList<string> SplitAttributes(string? attributes)
    {
        return (attributes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute))
            .ToList();
    }

    private static string NormalizeCatalogId(string? id)
    {
        var normalized = (id ?? string.Empty).Trim();
        return int.TryParse(normalized, out var value)
            ? value.ToString("000")
            : normalized;
    }

    private static int ParseEvolutionStage(string? stage)
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

    private sealed record ChainMetadata(string BaseName, int MaxStage);

    private sealed record NameParts(string BaseName, string DisplayName, string VariantName);
}
