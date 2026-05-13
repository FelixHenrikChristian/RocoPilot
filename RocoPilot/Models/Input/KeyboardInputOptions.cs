namespace RocoPilot.Models.Input;

public sealed class KeyboardInputOptions
{
    public int HoldDurationMs
    {
        get;
        init;
    } = 45;

    public int IntervalMs
    {
        get;
        init;
    } = 120;
}
