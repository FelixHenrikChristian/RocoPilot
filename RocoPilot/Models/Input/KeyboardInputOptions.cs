namespace RocoPilot.Models.Input;

public enum KeyboardInputMethod
{
    PostMessage = 0,
    SendInput = 1
}

public sealed class KeyboardInputOptions
{
    public KeyboardInputMethod Method
    {
        get;
        init;
    } = KeyboardInputMethod.PostMessage;

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
