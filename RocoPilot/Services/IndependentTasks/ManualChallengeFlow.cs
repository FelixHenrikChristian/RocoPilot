using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

internal enum ManualChallengeAction { None, OpenManual, Click, ScrollUp, ScrollDown, Approach, SelectChallenge, Interact, Battle, Capture, Complete, Escape, Stop }

internal readonly record struct ManualChallengeDecision(ManualChallengeAction Action, ImageMatchResult? Button = null);

// 动作只消费当前匹配画面；滚动前后行位置用于确认列表边界。
internal sealed class ManualChallengeFlow(FlowerSeedOption? target, int runCount = 6, string? legendaryTarget = null,
    bool useLegendaryMark = true)
{
    private enum ChallengePhase { Navigation, Starting, Battle, Settlement, Result }

    private readonly List<FlowerSeedOption> _options = [];
    private (string Name, double Y)[]? _beforeScroll;
    private int _unchangedScrolls;
    private bool _topConfirmed;
    private bool _targetRowSeen;
    private bool _targetMapSeen;
    private ChallengePhase _challengePhase;
    private bool _rewardsSeen;
    private bool _captureSeen;
    private (int Number, double Y, bool Matched)[] _visibleRows = [];
    private double _rowSpacing = double.PositiveInfinity;
    private bool _useLegendaryForCapture;

    public string? StopReason { get; private set; }

    public IReadOnlyList<FlowerSeedOption> Options => _options;
    public bool IsTopConfirmed => _topConfirmed;
    public int CompletedCount { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public int BattleNumber { get; private set; }
    public bool IsBattleActive => _challengePhase == ChallengePhase.Battle;

    public ManualChallengeDecision Next(ManualChallengeScreen screen, int clientHeight)
    {
        if (StopReason is not null)
            return screen.Scene switch
            {
                ManualChallengeScene.World or ManualChallengeScene.Interaction => new(ManualChallengeAction.Stop),
                ManualChallengeScene.InsufficientContractMark => Click(screen.ExitButton),
                ManualChallengeScene.LegendaryConfirmation => new(ManualChallengeAction.Escape),
                _ => default
            };
        if (screen.Scene is ManualChallengeScene.World or ManualChallengeScene.Interaction
            && _challengePhase is ChallengePhase.Battle or ChallengePhase.Result)
        {
            if (_challengePhase == ChallengePhase.Result && CompletedCount >= runCount)
                return new(ManualChallengeAction.Complete);
            if (_challengePhase == ChallengePhase.Battle && ++ConsecutiveFailures >= 3)
                throw new InvalidOperationException("挑战连续失败 3 次，任务已停止。请检查精灵状态后重新启动。");
            _challengePhase = ChallengePhase.Navigation;
            _rewardsSeen = false;
            _useLegendaryForCapture = false;
        }
        if ((target is not null || legendaryTarget is not null) && CompletedCount >= runCount && screen.Scene != ManualChallengeScene.Result) return default;

        switch (screen.Scene)
        {
            case ManualChallengeScene.World when _challengePhase == ChallengePhase.Navigation:
                return new(_targetMapSeen ? ManualChallengeAction.Approach : ManualChallengeAction.OpenManual);
            case ManualChallengeScene.Manual when _challengePhase == ChallengePhase.Navigation:
            case ManualChallengeScene.ChallengePage when _challengePhase == ChallengePhase.Navigation:
            case ManualChallengeScene.Confirmation when _targetMapSeen && _challengePhase == ChallengePhase.Navigation:
                return Click(screen.Button);
            case ManualChallengeScene.LegendaryConfirmation when _targetMapSeen
                && _challengePhase is ChallengePhase.Navigation or ChallengePhase.Result:
                return Click(screen.Button);
            case ManualChallengeScene.InsufficientContractMark when _targetMapSeen
                && _challengePhase is ChallengePhase.Navigation or ChallengePhase.Starting or ChallengePhase.Result:
                if (useLegendaryMark)
                {
                    _useLegendaryForCapture = true;
                    return Click(screen.Button);
                }
                StopReason = "契约印记或本周捕捉次数不足，已按配置停止。";
                return Click(screen.ExitButton);
            case ManualChallengeScene.LegendaryList when legendaryTarget is not null && _challengePhase == ChallengePhase.Navigation:
                var legendary = screen.Rows.FirstOrDefault(row => row.Name == legendaryTarget);
                if (legendary is null) return default;
                _targetRowSeen = true;
                return Click(legendary.Button);
            case ManualChallengeScene.FlowerList when _challengePhase == ChallengePhase.Navigation:
                if (screen.Rows.Count == 0) return default;
                var rows = ReadPositions(screen.Rows, clientHeight);
                if (_beforeScroll is { } previous)
                {
                    _unchangedScrolls = SameRows(previous, rows) ? _unchangedScrolls + 1 : 0;
                    _beforeScroll = null;
                }
                if (!_topConfirmed)
                {
                    if (_unchangedScrolls < 2) return new(ManualChallengeAction.ScrollUp);
                    _topConfirmed = true;
                    _unchangedScrolls = 0;
                }
                UpdateRowIdentities(screen.Rows, clientHeight);
                if (target is not null && screen.Rows.FirstOrDefault(row => row.Number == target.Number) is { } targetRow)
                {
                    _targetRowSeen = true;
                    return Click(targetRow.Button);
                }
                if (_unchangedScrolls < 2) return new(ManualChallengeAction.ScrollDown);
                if (target is not null)
                    throw new InvalidOperationException($"当前花种列表中未找到：{target.DisplayName}。");
                return new(ManualChallengeAction.Complete);
            case ManualChallengeScene.Map when _challengePhase == ChallengePhase.Navigation:
                if (!_targetRowSeen) return default;
                if (legendaryTarget is not null && screen.Text != legendaryTarget) return default;
                _targetMapSeen = true;
                return Click(screen.Button);
            case ManualChallengeScene.Interaction when _challengePhase == ChallengePhase.Navigation:
                if (!_targetMapSeen) return new(ManualChallengeAction.OpenManual);
                if (string.IsNullOrWhiteSpace(screen.Text)) return default;
                return new(ManualChallengeScreenRecognizer.Normalize(screen.Text) == "挑战"
                    ? ManualChallengeAction.Interact : ManualChallengeAction.SelectChallenge);
            case ManualChallengeScene.Preparation when _targetMapSeen
                && _challengePhase is ChallengePhase.Navigation or ChallengePhase.Starting or ChallengePhase.Result:
                if (screen.Button is not { IsMatch: true }) return default;
                _challengePhase = ChallengePhase.Starting;
                return Click(screen.Button);
            case ManualChallengeScene.MedalConfirmation when _challengePhase == ChallengePhase.Starting:
                return Click(screen.Button);
            case ManualChallengeScene.Battle when _challengePhase is ChallengePhase.Starting or ChallengePhase.Battle or ChallengePhase.Result:
                if (!IsBattleActive)
                {
                    _challengePhase = ChallengePhase.Battle;
                    _rewardsSeen = false;
                    _captureSeen = false;
                    BattleNumber++;
                }
                return new(ManualChallengeAction.Battle);
            case ManualChallengeScene.CaptureConfirmation when legendaryTarget is not null
                && _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement:
                _challengePhase = ChallengePhase.Settlement;
                if (screen.Marks is not { } marks) return default;
                // 本场只依据已经确认的不足提示选择印记，不再读取资源数值。
                if (_useLegendaryForCapture != marks.LegendarySelected)
                    return Click(_useLegendaryForCapture ? marks.LegendaryButton : marks.ContractButton);
                return Click(screen.Button);
            case ManualChallengeScene.LegendaryRewards when legendaryTarget is not null && _challengePhase == ChallengePhase.Settlement:
                return new(ManualChallengeAction.Escape);
            case ManualChallengeScene.Capture when _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement:
                // 专属球保证捕捉成功；之后短暂出现的大世界 HUD 仍属于结算过渡。
                _challengePhase = ChallengePhase.Settlement;
                _captureSeen = true;
                return new(ManualChallengeAction.Capture);
            case ManualChallengeScene.Rewards when _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement:
                if (screen.Button is not { IsMatch: true }) return default;
                _challengePhase = ChallengePhase.Settlement;
                _rewardsSeen = true;
                return Click(screen.Button);
            case ManualChallengeScene.Result when _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement or ChallengePhase.Result:
                if (_challengePhase != ChallengePhase.Result)
                {
                    if (legendaryTarget is not null ? !_captureSeen : !_rewardsSeen) return default;
                    CompletedCount++;
                    ConsecutiveFailures = 0;
                    _useLegendaryForCapture = false;
                    _challengePhase = ChallengePhase.Result;
                }
                return Click(CompletedCount >= runCount ? screen.ExitButton : screen.Button);
            default:
                return default;
        }
    }

    public void RecordScroll(ManualChallengeScreen screen, int clientHeight)
        => _beforeScroll = ReadPositions(screen.Rows, clientHeight);

    private void UpdateRowIdentities(IReadOnlyList<ManualChallengeRow> rows, int height)
    {
        var positions = ReadPositions(rows, height);
        for (var i = 1; i < positions.Length; i++)
            _rowSpacing = Math.Min(_rowSpacing, positions[i].Y - positions[i - 1].Y);

        List<(int Number, double Y, bool Matched)> current = [];
        foreach (var (row, position) in rows.Zip(positions))
        {
            // 向下扫描时条目只会上移；步长小于一行即可延续身份，容许匹配位置轻微抖动。
            var previous = _visibleRows.Where(candidate => candidate.Y >= position.Y - .003
                    && current.All(item => item.Number != candidate.Number))
                .OrderBy(candidate => Math.Abs(candidate.Y - position.Y)).FirstOrDefault();
            var sameRow = previous.Number != 0 && Math.Abs(previous.Y - position.Y)
                < (double.IsPositiveInfinity(_rowSpacing) ? .003 : _rowSpacing - .003);
            row.Number = sameRow ? previous.Number : _options.Count + 1;
            row.HasMatched = !string.IsNullOrWhiteSpace(row.Name) || sameRow && previous.Matched;
            var name = string.IsNullOrWhiteSpace(row.Name) ? row.RawName.Trim() : row.Name;
            if (!sameRow) _options.Add(new(row.Number, name));
            else if (!string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(_options[row.Number - 1].Name))
                _options[row.Number - 1] = new(row.Number, name);
            current.Add((row.Number, position.Y, row.HasMatched));
        }
        _visibleRows = current.ToArray();
    }

    private static ManualChallengeDecision Click(ImageMatchResult? button)
        => button is { IsMatch: true } ? new(ManualChallengeAction.Click, button) : default;

    private static (string Name, double Y)[] ReadPositions(IReadOnlyList<ManualChallengeRow> rows, int height)
        => rows.Select(row => (row.Name, (row.Button.Y + row.Button.Height / 2d) / height)).ToArray();

    private static bool SameRows((string Name, double Y)[] first, (string Name, double Y)[] second)
        => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First.Y - pair.Second.Y) <= .003
            && (string.IsNullOrWhiteSpace(pair.First.Name) || string.IsNullOrWhiteSpace(pair.Second.Name)
                || pair.First.Name == pair.Second.Name));
}
