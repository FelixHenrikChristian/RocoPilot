namespace RocoPilot.Models.Runtime;

public sealed class AutoBattleSettings
{
    public const string DefaultRoundOrder = "1, 2, 3, 4, X";
    public const string DefaultTurnSequence = "{skill}";

    public bool IsEnabled
    {
        get;
        set;
    }

    public string RoundOrder
    {
        get;
        set;
    } = DefaultRoundOrder;

    public string TurnSequence
    {
        get;
        set;
    } = DefaultTurnSequence;

    public List<AutoBattleReleaseStep> ReleaseSequence
    {
        get;
        set;
    } = CreateDefaultReleaseSequence();

    public List<AutoBattleTurnSequencePreset> TurnSequencePresets
    {
        get;
        set;
    } = [];

    public bool OnlyRecoverEnergyAfterEncounterRelieved
    {
        get;
        set;
    }

    public static AutoBattleSettings CreateDefault()
    {
        return new AutoBattleSettings
        {
            IsEnabled = false,
            RoundOrder = DefaultRoundOrder,
            TurnSequence = DefaultTurnSequence,
            ReleaseSequence = CreateDefaultReleaseSequence(),
            TurnSequencePresets = [],
            OnlyRecoverEnergyAfterEncounterRelieved = false
        };
    }

    public static List<AutoBattleReleaseStep> CreateDefaultReleaseSequence()
    {
        return
        [
            AutoBattleReleaseStep.CreateSkill("1"),
            AutoBattleReleaseStep.CreateSkill("2"),
            AutoBattleReleaseStep.CreateSkill("3"),
            AutoBattleReleaseStep.CreateSkill("4"),
            AutoBattleReleaseStep.CreateSkill("X")
        ];
    }

    public AutoBattleSettings Clone()
    {
        return new AutoBattleSettings
        {
            IsEnabled = IsEnabled,
            RoundOrder = RoundOrder,
            TurnSequence = TurnSequence,
            ReleaseSequence = (ReleaseSequence ?? []).Select(step => step.Clone()).ToList(),
            TurnSequencePresets = (TurnSequencePresets ?? []).Select(preset => preset.Clone()).ToList(),
            OnlyRecoverEnergyAfterEncounterRelieved = OnlyRecoverEnergyAfterEncounterRelieved
        };
    }
}

public sealed class AutoBattleReleaseStep
{
    public bool IsCustom
    {
        get;
        set;
    }

    public string SkillKey
    {
        get;
        set;
    } = "1";

    public string Name
    {
        get;
        set;
    } = string.Empty;

    public string Sequence
    {
        get;
        set;
    } = string.Empty;

    public static AutoBattleReleaseStep CreateSkill(string skillKey)
    {
        return new AutoBattleReleaseStep
        {
            IsCustom = false,
            SkillKey = skillKey,
            Name = skillKey,
            Sequence = string.Empty
        };
    }

    public static AutoBattleReleaseStep CreateCustom(string name, string sequence)
    {
        return new AutoBattleReleaseStep
        {
            IsCustom = true,
            SkillKey = string.Empty,
            Name = name,
            Sequence = sequence
        };
    }

    public AutoBattleReleaseStep Clone()
    {
        return new AutoBattleReleaseStep
        {
            IsCustom = IsCustom,
            SkillKey = SkillKey,
            Name = Name,
            Sequence = Sequence
        };
    }
}

public sealed class AutoBattleTurnSequencePreset
{
    public string Name
    {
        get;
        set;
    } = string.Empty;

    public string Sequence
    {
        get;
        set;
    } = string.Empty;

    public AutoBattleTurnSequencePreset Clone()
    {
        return new AutoBattleTurnSequencePreset
        {
            Name = Name,
            Sequence = Sequence
        };
    }
}
