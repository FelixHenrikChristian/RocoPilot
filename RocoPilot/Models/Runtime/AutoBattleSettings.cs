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

    public static AutoBattleSettings CreateDefault()
    {
        return new AutoBattleSettings
        {
            IsEnabled = false,
            RoundOrder = DefaultRoundOrder,
            TurnSequence = DefaultTurnSequence
        };
    }

    public AutoBattleSettings Clone()
    {
        return new AutoBattleSettings
        {
            IsEnabled = IsEnabled,
            RoundOrder = RoundOrder,
            TurnSequence = TurnSequence
        };
    }
}
