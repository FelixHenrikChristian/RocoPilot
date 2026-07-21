using Newtonsoft.Json;

using RocoPilot.Models.Input;

namespace RocoPilot.Models.Runtime;

public sealed class AutoBattleSettings
{
    public const string DefaultRoundOrder = "1, 2, 3, 4, X";
    public const string DefaultTurnSequence = "{skill}";
    public const string DefaultBossComboSequence = "1, X, 2, X, 3, X";
    public const int BossComboSkillCount = 6;
    public const int DefaultSkillSelectionActionDelayMs = 500;
    public const int DefaultSkillSelectionRetryDelayMs = 4000;
    public const int DefaultKeyboardHoldDurationMs = 100;
    public const int DefaultKeyboardIntervalMs = 500;
    public const int DefaultCaptureKeyboardIntervalMs = 500;

    public const int MinimumDelayMs = 0;
    public const int MaximumDelayMs = 30000;
    public const int MinimumRetryDelayMs = 500;
    public const int MinimumKeyboardHoldDurationMs = 10;
    public const int MaximumKeyboardHoldDurationMs = 2000;

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

    public string BossComboSequence
    {
        get;
        set;
    } = DefaultBossComboSequence;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<AutoBattleReleaseStep> ReleaseSequence
    {
        get;
        set;
    } = CreateDefaultReleaseSequence();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<AutoBattleReleaseStep> BossReleaseSequence
    {
        get;
        set;
    } = [];

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<AutoBattleTurnSequencePreset> TurnSequencePresets
    {
        get;
        set;
    } = [];

    public AutoBattleEncounterRelievedAction EncounterRelievedAction
    {
        get;
        set;
    } = AutoBattleEncounterRelievedAction.RecoverEnergy;

    public KeyboardInputMethod KeyboardInputMethod
    {
        get;
        set;
    } = KeyboardInputMethod.PostMessage;

    public int SkillSelectionActionDelayMs
    {
        get;
        set;
    } = DefaultSkillSelectionActionDelayMs;

    public int SkillSelectionRetryDelayMs
    {
        get;
        set;
    } = DefaultSkillSelectionRetryDelayMs;

    public int KeyboardHoldDurationMs
    {
        get;
        set;
    } = DefaultKeyboardHoldDurationMs;

    public int KeyboardIntervalMs
    {
        get;
        set;
    } = DefaultKeyboardIntervalMs;

    public int CaptureKeyboardIntervalMs
    {
        get;
        set;
    } = DefaultCaptureKeyboardIntervalMs;

    public static AutoBattleSettings CreateDefault()
    {
        return new AutoBattleSettings
        {
            IsEnabled = false,
            RoundOrder = DefaultRoundOrder,
            TurnSequence = DefaultTurnSequence,
            BossComboSequence = DefaultBossComboSequence,
            ReleaseSequence = CreateDefaultReleaseSequence(),
            BossReleaseSequence = CreateDefaultReleaseSequence(),
            TurnSequencePresets = [],
            EncounterRelievedAction = AutoBattleEncounterRelievedAction.RecoverEnergy,
            KeyboardInputMethod = KeyboardInputMethod.PostMessage,
            SkillSelectionActionDelayMs = DefaultSkillSelectionActionDelayMs,
            SkillSelectionRetryDelayMs = DefaultSkillSelectionRetryDelayMs,
            KeyboardHoldDurationMs = DefaultKeyboardHoldDurationMs,
            KeyboardIntervalMs = DefaultKeyboardIntervalMs,
            CaptureKeyboardIntervalMs = DefaultCaptureKeyboardIntervalMs
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
            BossComboSequence = BossComboSequence,
            ReleaseSequence = (ReleaseSequence ?? []).Select(step => step.Clone()).ToList(),
            BossReleaseSequence = (BossReleaseSequence ?? []).Select(step => step.Clone()).ToList(),
            TurnSequencePresets = (TurnSequencePresets ?? []).Select(preset => preset.Clone()).ToList(),
            EncounterRelievedAction = EncounterRelievedAction,
            KeyboardInputMethod = KeyboardInputMethod,
            SkillSelectionActionDelayMs = SkillSelectionActionDelayMs,
            SkillSelectionRetryDelayMs = SkillSelectionRetryDelayMs,
            KeyboardHoldDurationMs = KeyboardHoldDurationMs,
            KeyboardIntervalMs = KeyboardIntervalMs,
            CaptureKeyboardIntervalMs = CaptureKeyboardIntervalMs
        };
    }
}

public enum AutoBattleEncounterRelievedAction
{
    NoAction = 0,
    RecoverEnergy = 1,
    ReleaseSkill = 2,
    Capture = 3
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
