using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

internal enum FlowerSeedAction { None, OpenManual, Click, ScrollUp, ScrollDown, Approach, SelectChallenge, Interact, Battle, Capture, Complete }

internal readonly record struct FlowerSeedDecision(FlowerSeedAction Action, ImageMatchResult? Button = null);

// 动作只消费当前匹配画面；滚动前后行位置用于确认列表边界。
internal sealed class FlowerSeedFlow(FlowerSeedOption? target, int runCount = 6)
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
    private (int Number, double Y, bool Matched)[] _visibleRows = [];
    private double _rowSpacing = double.PositiveInfinity;

    public IReadOnlyList<FlowerSeedOption> Options => _options;
    public bool IsTopConfirmed => _topConfirmed;
    public int CompletedCount { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public int BattleNumber { get; private set; }
    public bool IsBattleActive => _challengePhase == ChallengePhase.Battle;

    public FlowerSeedDecision Next(FlowerSeedScreen screen, int clientHeight)
    {
        if (screen.Scene is FlowerSeedScene.World or FlowerSeedScene.Interaction
            && _challengePhase is ChallengePhase.Battle or ChallengePhase.Result)
        {
            if (_challengePhase == ChallengePhase.Result && CompletedCount >= runCount)
                return new(FlowerSeedAction.Complete);
            if (_challengePhase == ChallengePhase.Battle && ++ConsecutiveFailures >= 3)
                throw new InvalidOperationException("花种挑战连续失败 3 次，任务已停止。请检查精灵状态后重新启动。");
            _challengePhase = ChallengePhase.Navigation;
            _rewardsSeen = false;
        }
        if (target is not null && CompletedCount >= runCount && screen.Scene != FlowerSeedScene.Result) return default;

        switch (screen.Scene)
        {
            case FlowerSeedScene.World when _challengePhase == ChallengePhase.Navigation:
                return new(_targetMapSeen ? FlowerSeedAction.Approach : FlowerSeedAction.OpenManual);
            case FlowerSeedScene.Manual when _challengePhase == ChallengePhase.Navigation:
            case FlowerSeedScene.ChallengePage when _challengePhase == ChallengePhase.Navigation:
            case FlowerSeedScene.Confirmation when _targetMapSeen && _challengePhase == ChallengePhase.Navigation:
                return Click(screen.Button);
            case FlowerSeedScene.FlowerList when _challengePhase == ChallengePhase.Navigation:
                if (screen.Rows.Count == 0) return default;
                var rows = ReadPositions(screen.Rows, clientHeight);
                if (_beforeScroll is { } previous)
                {
                    _unchangedScrolls = SameRows(previous, rows) ? _unchangedScrolls + 1 : 0;
                    _beforeScroll = null;
                }
                if (!_topConfirmed)
                {
                    if (_unchangedScrolls < 2) return new(FlowerSeedAction.ScrollUp);
                    _topConfirmed = true;
                    _unchangedScrolls = 0;
                }
                UpdateRowIdentities(screen.Rows, clientHeight);
                if (target is not null && screen.Rows.FirstOrDefault(row => row.Number == target.Number) is { } targetRow)
                {
                    _targetRowSeen = true;
                    return Click(targetRow.Button);
                }
                if (_unchangedScrolls < 2) return new(FlowerSeedAction.ScrollDown);
                if (target is not null)
                    throw new InvalidOperationException($"当前花种列表中未找到：{target.DisplayName}。");
                return new(FlowerSeedAction.Complete);
            case FlowerSeedScene.Map when _challengePhase == ChallengePhase.Navigation:
                if (!_targetRowSeen) return default;
                _targetMapSeen = true;
                return Click(screen.Button);
            case FlowerSeedScene.Interaction when _challengePhase == ChallengePhase.Navigation:
                if (!_targetMapSeen) return new(FlowerSeedAction.OpenManual);
                if (string.IsNullOrWhiteSpace(screen.Text)) return default;
                return new(FlowerSeedScreenRecognizer.Normalize(screen.Text) == "挑战"
                    ? FlowerSeedAction.Interact : FlowerSeedAction.SelectChallenge);
            case FlowerSeedScene.Preparation when _targetMapSeen
                && _challengePhase is ChallengePhase.Navigation or ChallengePhase.Starting:
                if (screen.Button is not { IsMatch: true }) return default;
                _challengePhase = ChallengePhase.Starting;
                return Click(screen.Button);
            case FlowerSeedScene.MedalConfirmation when _challengePhase == ChallengePhase.Starting:
                return Click(screen.Button);
            case FlowerSeedScene.Battle when _challengePhase is ChallengePhase.Starting or ChallengePhase.Battle or ChallengePhase.Result:
                if (!IsBattleActive)
                {
                    _challengePhase = ChallengePhase.Battle;
                    _rewardsSeen = false;
                    BattleNumber++;
                }
                return new(FlowerSeedAction.Battle);
            case FlowerSeedScene.Capture when _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement:
                // 专属球保证捕捉成功；之后短暂出现的大世界 HUD 仍属于结算过渡。
                _challengePhase = ChallengePhase.Settlement;
                return new(FlowerSeedAction.Capture);
            case FlowerSeedScene.Rewards when _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement:
                if (screen.Button is not { IsMatch: true }) return default;
                _challengePhase = ChallengePhase.Settlement;
                _rewardsSeen = true;
                return Click(screen.Button);
            case FlowerSeedScene.Result when _challengePhase is ChallengePhase.Battle or ChallengePhase.Settlement or ChallengePhase.Result:
                if (_challengePhase != ChallengePhase.Result)
                {
                    if (!_rewardsSeen) return default;
                    CompletedCount++;
                    ConsecutiveFailures = 0;
                    _challengePhase = ChallengePhase.Result;
                }
                return Click(CompletedCount >= runCount ? screen.ExitButton : screen.Button);
            default:
                return default;
        }
    }

    public void RecordScroll(FlowerSeedScreen screen, int clientHeight)
        => _beforeScroll = ReadPositions(screen.Rows, clientHeight);

    private void UpdateRowIdentities(IReadOnlyList<FlowerSeedRow> rows, int height)
    {
        var positions = ReadPositions(rows, height);
        for (var i = 1; i < positions.Length; i++)
            _rowSpacing = Math.Min(_rowSpacing, positions[i].Y - positions[i - 1].Y);

        List<(int Number, double Y, bool Matched)> current = [];
        foreach (var (row, position) in rows.Zip(positions))
        {
            // 扫描每次只滚动小于半行的距离，邻近位置可延续可见花种的身份。
            var previous = _visibleRows.Where(candidate => current.All(item => item.Number != candidate.Number))
                .OrderBy(candidate => Math.Abs(candidate.Y - position.Y)).FirstOrDefault();
            var sameRow = previous.Number != 0 && Math.Abs(previous.Y - position.Y)
                < (double.IsPositiveInfinity(_rowSpacing) ? .003 : _rowSpacing / 2);
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

    private static FlowerSeedDecision Click(ImageMatchResult? button)
        => button is { IsMatch: true } ? new(FlowerSeedAction.Click, button) : default;

    private static (string Name, double Y)[] ReadPositions(IReadOnlyList<FlowerSeedRow> rows, int height)
        => rows.Select(row => (row.Name, (row.Button.Y + row.Button.Height / 2d) / height)).ToArray();

    private static bool SameRows((string Name, double Y)[] first, (string Name, double Y)[] second)
        => first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First.Y - pair.Second.Y) <= .003
            && (string.IsNullOrWhiteSpace(pair.First.Name) || string.IsNullOrWhiteSpace(pair.Second.Name)
                || pair.First.Name == pair.Second.Name));
}
