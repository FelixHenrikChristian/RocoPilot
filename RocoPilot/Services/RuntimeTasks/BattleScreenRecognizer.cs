using RocoPilot.Configuration;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.RuntimeTasks;

public enum BattleScreen { SkillSelection, PetSwitching, Chat, Unknown, PetSwitchConfirmation }

public sealed class BattleScreenRecognizer(RuntimeFrameRecognizer recognizer)
{
    private const string BattleChatTemplateName = "battle-chat.png";
    private const string BattleSkillTemplateName = "battle-button-skill.png";
    private const string BattleChangeTemplateName = "battle-button-change.png";

    private static readonly string[] BattleChatRegionIds =
    [
        RecognitionRegionIds.BattleChatButton
    ];
    private static readonly string[] BattleSkillRegionIds =
    [
        RecognitionRegionIds.BattleSkillButton
    ];
    private static readonly string[] BattleChangeRegionIds =
    [
        RecognitionRegionIds.BattleChangeButton
    ];
    private static readonly ImageMatchOptions BattleChatMatchOptions = new()
    {
        MinimumScore = 0.96,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleSkillMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions BattleChangeMatchOptions = new()
    {
        MinimumScore = 0.88,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    public async Task<bool> IsBattlePetSwitchingAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleChangeRegionIds,
            BattleChangeTemplateName,
            BattleChangeMatchOptions,
            cancellationToken))
        {
            return true;
        }

        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSkillRegionIds,
            BattleChangeTemplateName,
            BattleChangeMatchOptions,
            cancellationToken);
    }

    public async Task<bool> IsBattleSkillSelectionVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleSkillRegionIds,
            BattleSkillTemplateName,
            BattleSkillMatchOptions,
            cancellationToken);
    }

    public async Task<bool> IsBattleChatVisibleAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        return await recognizer.MatchRuntimeTemplateAsync(
            state,
            frame,
            BattleChatRegionIds,
            BattleChatTemplateName,
            BattleChatMatchOptions,
            cancellationToken);
    }

    public async Task<BattleScreen> RecognizeAsync(RuntimeTaskState state, CapturedFrame frame, CancellationToken token)
    {
        if (await IsBattleSkillSelectionVisibleAsync(state, frame, token)) return BattleScreen.SkillSelection;
        if (await IsBattlePetSwitchingAsync(state, frame, token)) return BattleScreen.PetSwitching;
        return await IsBattleChatVisibleAsync(state, frame, token) ? BattleScreen.Chat : BattleScreen.Unknown;
    }
}
