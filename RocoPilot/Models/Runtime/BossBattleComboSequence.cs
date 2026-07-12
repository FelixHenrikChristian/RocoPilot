namespace RocoPilot.Models.Runtime;

internal static class BossBattleComboSequence
{
    private static readonly char[] Separators = [',', ';', '\r', '\n', ' ', '\t'];

    public static bool TryNormalize(string? sequence, out string normalizedSequence)
    {
        var skillKeys = Parse(sequence);
        if (skillKeys.Count != AutoBattleSettings.BossComboSkillCount
            || skillKeys.Any(skillKey => skillKey is not ("1" or "2" or "3" or "4" or "X")))
        {
            normalizedSequence = string.Empty;
            return false;
        }

        normalizedSequence = string.Join(", ", skillKeys);
        return true;
    }

    public static string NormalizeOrDefault(string? sequence)
    {
        return TryNormalize(sequence, out var normalizedSequence)
            ? normalizedSequence
            : AutoBattleSettings.DefaultBossComboSequence;
    }

    public static IReadOnlyList<string> ParseOrDefault(string? sequence)
    {
        return Parse(NormalizeOrDefault(sequence));
    }

    public static string BuildConfirmedSequence(string? sequence)
    {
        return $"{NormalizeOrDefault(sequence)}, Space";
    }

    private static IReadOnlyList<string> Parse(string? sequence)
    {
        if (string.IsNullOrWhiteSpace(sequence))
        {
            return [];
        }

        return sequence
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(skillKey => skillKey.ToUpperInvariant())
            .ToArray();
    }
}
