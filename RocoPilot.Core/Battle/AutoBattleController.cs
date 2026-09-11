#nullable enable

namespace RocoPilot.Core.Battle;

public enum AutoBattlePhase { Idle, SkillSelection, PetSwitching }
public enum AutoBattleAction { None, NoAction, Skill, EnergyRecovery, Capture }

public sealed record AutoBattleTurn(
    long Id,
    int Number,
    DateTimeOffset StartedAt,
    AutoBattleReleaseStep ReleaseStep,
    bool EnemyNameResolved = false,
    AutoBattleAction Action = AutoBattleAction.None,
    DateTimeOffset? LastActionAt = null);

public sealed record AutoBattlePlan(
    AutoBattleAction Action,
    string Sequence,
    string Description,
    string DisplayKey,
    string? FallbackSequence = null)
{
    public bool ShouldSendKeys => Action is AutoBattleAction.Skill or AutoBattleAction.EnergyRecovery or AutoBattleAction.Capture;
}

/// <summary>
/// 战斗状态的唯一写入入口。所有时间由调用方传入；本类不进行 OCR、按键、存储或界面操作。
/// 异步识别携带 BattleId/TurnId，已结束战斗或回合的结果不会进入新状态。
/// </summary>
public sealed class AutoBattleController
{
    private static readonly TimeSpan BloodlineWaitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SkillFailureCheckDelay = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private long _battleId;
    private long _nextTurnId;
    private int _roundIndex;
    private int _turnNumber;
    private AutoBattlePhase _phase;
    private AutoBattleTurn? _turn;
    private bool _suspendedForShiny;
    private bool _encounterRelieved;
    private EncounterBloodlineKind _bloodline = EncounterBloodlineKind.Unrecognized;
    private DateTimeOffset? _bloodlineWaitStartedAt;
    private (EncounterBloodlineKind Kind, bool Capture)? _captureDecision;

    public long BattleId { get { lock (_gate) return _battleId; } }
    public int RoundIndex { get { lock (_gate) return _roundIndex; } }
    public int TurnNumber { get { lock (_gate) return _turnNumber; } }
    public AutoBattlePhase Phase { get { lock (_gate) return _phase; } }
    public bool IsSuspendedForShiny { get { lock (_gate) return _suspendedForShiny; } }
    public bool IsEncounterRelieved { get { lock (_gate) return _encounterRelieved; } }
    public AutoBattleTurn? CurrentTurn
    {
        get
        {
            lock (_gate)
                return _turn is null ? null : _turn with { ReleaseStep = _turn.ReleaseStep.Clone() };
        }
    }

    public AutoBattleTurn BeginSkillSelection(AutoBattleSettings settings, DateTimeOffset now)
    {
        lock (_gate)
        {
            _phase = AutoBattlePhase.SkillSelection;
            _turn = new AutoBattleTurn(++_nextTurnId, ++_turnNumber, now, CurrentReleaseStep(settings));
            return _turn with { ReleaseStep = _turn.ReleaseStep.Clone() };
        }
    }

    public bool IsEnemyNameRecognitionDue(AutoBattleSettings settings, DateTimeOffset now)
    {
        lock (_gate)
            return _turn is { EnemyNameResolved: false } turn
                && now - turn.StartedAt >= TimeSpan.FromMilliseconds(settings.SkillSelectionActionDelayMs);
    }

    public bool ConfirmEnemyName(long turnId)
    {
        lock (_gate)
        {
            if (_turn is null || _turn.Id != turnId) return false;
            _turn = _turn with { EnemyNameResolved = true };
            return true;
        }
    }

    public bool CanAct(AutoBattleSettings settings, DateTimeOffset now)
    {
        lock (_gate)
            return settings.IsEnabled && !_suspendedForShiny
                && _turn is { EnemyNameResolved: true } turn
                && now - turn.StartedAt >= TimeSpan.FromMilliseconds(settings.SkillSelectionActionDelayMs)
                && (turn.LastActionAt is null
                    || now - turn.LastActionAt.Value >= TimeSpan.FromMilliseconds(settings.SkillSelectionRetryDelayMs));
    }

    public bool ShouldRecoverAfterSkillFailure(AutoBattleSettings settings, DateTimeOffset now)
    {
        lock (_gate)
            return settings.IsEnabled && !_suspendedForShiny
                && _turn is { Action: AutoBattleAction.Skill, LastActionAt: { } lastActionAt }
                && now - lastActionAt >= SkillFailureCheckDelay;
    }

    public AutoBattlePlan PlanSkillSelection(AutoBattleSettings settings, bool bloodlineRecognitionAvailable, DateTimeOffset now)
    {
        lock (_gate)
        {
            var step = _turn?.ReleaseStep ?? CurrentReleaseStep(settings);
            if (_encounterRelieved && AutoBattleSettingsRules.RequiresReliefDetection(settings.EncounterRelievedAction))
            {
                switch (settings.EncounterRelievedAction)
                {
                    case AutoBattleEncounterRelievedAction.NoAction:
                        return new(AutoBattleAction.NoAction, "", "无操作，检测到奇遇效果解除，等待手动释放技能", "-");
                    case AutoBattleEncounterRelievedAction.RecoverEnergy:
                        return new(AutoBattleAction.EnergyRecovery, "X", "奇遇解除后回能 X", "X");
                    case AutoBattleEncounterRelievedAction.Capture:
                        return PlanCapture(settings, step, bloodlineRecognitionAvailable, now);
                }
            }

            return PlanSkill(settings, step);
        }
    }

    public bool RecordAction(long turnId, AutoBattleAction action, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_suspendedForShiny || _turn is null || _turn.Id != turnId) return false;
            _turn = _turn with { Action = action, LastActionAt = now };
            return true;
        }
    }

    public void CompleteSkillSelection()
    {
        lock (_gate)
        {
            if (_turn?.Action == AutoBattleAction.Skill) _roundIndex++;
            _turn = null;
            if (_phase == AutoBattlePhase.SkillSelection) _phase = AutoBattlePhase.Idle;
        }
    }

    public AutoBattleReleaseStep BeginPetSwitching(AutoBattleSettings settings)
    {
        lock (_gate)
        {
            _phase = AutoBattlePhase.PetSwitching;
            _turn = null;
            _turnNumber++;
            return CurrentReleaseStep(settings);
        }
    }

    public void ObservePetSwitching(bool visible)
    {
        lock (_gate)
        {
            if (visible) _phase = AutoBattlePhase.PetSwitching;
            else if (_phase == AutoBattlePhase.PetSwitching) _phase = AutoBattlePhase.Idle;
        }
    }

    public bool ObserveEncounterRelieved(AutoBattleSettings settings)
    {
        lock (_gate)
        {
            if (_suspendedForShiny || _encounterRelieved
                || !AutoBattleSettingsRules.RequiresReliefDetection(settings.EncounterRelievedAction)) return false;
            _encounterRelieved = true;
            return true;
        }
    }

    public bool ObserveShiny(long battleId)
    {
        lock (_gate)
        {
            if (battleId != _battleId || _suspendedForShiny) return false;
            _suspendedForShiny = true;
            _turn = null;
            if (_phase == AutoBattlePhase.SkillSelection) _phase = AutoBattlePhase.Idle;
            return true;
        }
    }

    public void ObserveBloodline(long battleId, EncounterBloodlineKind kind)
    {
        lock (_gate)
        {
            if (battleId != _battleId || _bloodline != EncounterBloodlineKind.Unrecognized
                || _captureDecision is not null || kind == EncounterBloodlineKind.Unrecognized) return;
            _bloodline = kind;
            _bloodlineWaitStartedAt = null;
        }
    }

    public void ResetEncounterRelief()
    {
        lock (_gate)
        {
            _encounterRelieved = false;
            _bloodline = EncounterBloodlineKind.Unrecognized;
            _bloodlineWaitStartedAt = null;
            _captureDecision = null;
        }
    }

    public void ResetBattle()
    {
        lock (_gate)
        {
            _battleId++;
            _roundIndex = 0;
            _turnNumber = 0;
            _turn = null;
            _phase = AutoBattlePhase.Idle;
            _suspendedForShiny = false;
            ResetEncounterRelief();
        }
    }

    private AutoBattleReleaseStep CurrentReleaseStep(AutoBattleSettings settings)
    {
        if (_roundIndex >= settings.ReleaseSequence.Count) _roundIndex = 0;
        return settings.ReleaseSequence[_roundIndex].Clone();
    }

    private AutoBattlePlan PlanCapture(AutoBattleSettings settings, AutoBattleReleaseStep step, bool available, DateTimeOffset now)
    {
        var filter = settings.BloodlineCaptureFilter;
        if (!filter.IsEnabled)
            return new(AutoBattleAction.Capture, "W, 1, Space", "奇遇解除后捕捉 W, 1, Space", "W, 1, Space");

        if (_captureDecision is null)
        {
            if (_bloodline == EncounterBloodlineKind.Unrecognized && available)
            {
                _bloodlineWaitStartedAt ??= now;
                if (now - _bloodlineWaitStartedAt.Value < BloodlineWaitTimeout)
                    return new(AutoBattleAction.None, "", "等待血脉提示识别", "-");
            }
            _captureDecision = (_bloodline, filter.ShouldCapture(_bloodline));
        }

        var (kind, capture) = _captureDecision.Value;
        var name = kind switch
        {
            EncounterBloodlineKind.QiYi => "奇异",
            EncounterBloodlineKind.HunXue => "混血",
            EncounterBloodlineKind.WuRan => "污染",
            EncounterBloodlineKind.Normal => "普通",
            _ => "未识别"
        };
        return capture
            ? new(AutoBattleAction.Capture, "W, 1, Space", $"奇遇解除后捕捉（血脉：{name}） W, 1, Space", "W, 1, Space")
            : PlanSkill(settings, step) with { Description = $"奇遇解除后血脉不符（{name}），释放战技" };
    }

    private static AutoBattlePlan PlanSkill(AutoBattleSettings settings, AutoBattleReleaseStep step)
    {
        var display = AutoBattleSettingsRules.GetReleaseStepDisplay(step);
        return new(AutoBattleAction.Skill, AutoBattleSettingsRules.BuildReleaseSequence(settings, step),
            step.IsCustom ? $"执行自定义序列 {display}" : $"释放技能 {display}", display,
            step.IsCustom ? null : step.SkillKey);
    }
}
