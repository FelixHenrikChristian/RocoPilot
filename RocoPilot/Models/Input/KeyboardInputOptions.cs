namespace RocoPilot.Models.Input;

public sealed class KeyboardInputOptions
{
    public KeyboardInputDeliveryMode DeliveryMode
    {
        get;
        init;
    } = KeyboardInputDeliveryMode.ForegroundInput;

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

    public int ForegroundActivationDelayMs
    {
        get;
        init;
    } = 150;

    public bool ActivateWindowForForegroundInput
    {
        get;
        init;
    } = true;
}
