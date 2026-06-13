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
        var normalizedStage = NormalizeStage(stage);
        if (normalizedStage.Length == 0)
        {
            return 0;
        }

        if (normalizedStage.Contains('Ⅴ'))
        {
            return 5;
        }

        if (normalizedStage.Contains('Ⅳ'))
        {
            return 4;
        }

        if (normalizedStage.Contains('Ⅲ'))
        {
            return 3;
        }

        if (normalizedStage.Contains('Ⅱ'))
        {
            return 2;
        }

        if (normalizedStage.Contains('Ⅰ'))
        {
            return 1;
        }

        return normalizedStage.Contains("最终", StringComparison.Ordinal)
            || normalizedStage.Contains("首领", StringComparison.Ordinal)
            ? 90
            : 10;
    }

    public static string NormalizeStage(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return string.Empty;
        }

        var normalized = stage.Trim();
        if (normalized.Contains("最终", StringComparison.Ordinal))
        {
            return "最终形态";
        }

        if (normalized.Contains("首领", StringComparison.Ordinal))
        {
            return "首领形态";
        }

        if (normalized.Contains('Ⅳ') || normalized.Contains("IV", StringComparison.Ordinal))
        {
            return "Ⅳ阶";
        }

        if (normalized.Contains('Ⅴ') || normalized.Contains("V", StringComparison.Ordinal))
        {
            return "Ⅴ阶";
        }

        if (normalized.Contains('Ⅲ') || normalized.Contains("III", StringComparison.Ordinal)
            || normalized.Contains('三'))
        {
            return "Ⅲ阶";
        }

        if (normalized.Contains('Ⅱ') || normalized.Contains("II", StringComparison.Ordinal)
            || normalized.Contains('二'))
        {
            return "Ⅱ阶";
        }

        if (normalized.Contains('Ⅰ') || normalized.Contains("I", StringComparison.Ordinal)
            || normalized.Contains('一'))
        {
            return "Ⅰ阶";
        }

        return normalized;
    }

    public static int ParseCatalogId(string id)
    {
        return int.TryParse(id, out var value) ? value : int.MaxValue;
    }
}
