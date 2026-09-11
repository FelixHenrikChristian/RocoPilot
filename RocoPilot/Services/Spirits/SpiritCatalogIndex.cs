using RocoPilot.Helpers;
using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal sealed class SpiritCatalogIndex
{
    private readonly IReadOnlyList<SpiritNameMatchCandidate> _candidates;
    private readonly Dictionary<string, string> _recordNames = new(StringComparer.OrdinalIgnoreCase);

    public SpiritCatalogIndex(SpiritCatalogDocument document)
    {
        _candidates = BuildNameMatchCandidates(document);
        foreach (var item in document.Spirits)
        {
            var representative = ResolveRepresentativeName(document, item, item.BaseId, item.BaseName);
            var recordName = string.IsNullOrWhiteSpace(representative) ? BuildDisplayName(item) : representative;
            foreach (var name in new[] { item.Name, item.WikiName, BuildDisplayName(item) }.Concat(item.Aliases))
            {
                var key = TextMatchingHelper.NormalizeSpiritNameForMatching(name);
                if (key.Length > 0) _recordNames.TryAdd(key, recordName);
            }
        }
    }

    public string Match(string recognizedText, double minimumSimilarity)
    {
        var query = TextMatchingHelper.NormalizeSpiritNameForMatching(recognizedText);
        if (query.Length == 0) return string.Empty;
        var threshold = Math.Clamp(minimumSimilarity, 0, 1);
        SpiritNameMatchCandidate? bestCandidate = null;
        var bestSimilarity = -1d;
        foreach (var candidate in _candidates)
        {
            foreach (var searchName in candidate.SearchNames)
            {
                var similarity = TextMatchingHelper.CalculateSimilarity(query, searchName);
                if (similarity <= bestSimilarity) continue;
                bestSimilarity = similarity;
                bestCandidate = candidate;
                if (similarity >= 1) return candidate.Name;
            }
        }
        if (bestCandidate is null) return threshold <= 0 ? query : string.Empty;
        return threshold <= 0 || bestSimilarity >= threshold ? bestCandidate.Name : string.Empty;
    }

    public string ResolveEvolutionRecordName(string spiritName)
    {
        var key = TextMatchingHelper.NormalizeSpiritNameForMatching(spiritName);
        if (key.Length == 0) return string.Empty;
        return _recordNames.TryGetValue(key, out var recordName)
            ? recordName : TextMatchingHelper.NormalizeSpiritNameForDisplay(spiritName);
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

    private sealed record SpiritNameMatchCandidate(string Name, IReadOnlyList<string> SearchNames);
}
