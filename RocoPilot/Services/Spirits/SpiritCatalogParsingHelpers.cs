using RocoPilot.Helpers;
using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal static class SpiritCatalogParsingHelpers
{
    public static List<string> BuildAliases(
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
            var displayName = TextMatchingHelper.NormalizeSpiritNameForDisplay(item.WikiName);
            var aliasBaseName = displayName.Length > 0 ? displayName : item.WikiName;
            aliases.Add($"{aliasBaseName}（{item.RegionalForm}）");
            aliases.Add($"{aliasBaseName}({item.RegionalForm})");
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .OrderBy(alias => alias, StringComparer.Ordinal)
            .ToList();
    }

    public static int StageRank(string stage)
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

    public static int ParseCatalogId(string id)
    {
        return int.TryParse(id, out var value) ? value : int.MaxValue;
    }
}
