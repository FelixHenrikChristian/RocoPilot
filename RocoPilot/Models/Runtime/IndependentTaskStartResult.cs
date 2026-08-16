namespace RocoPilot.Models.Runtime;

public sealed class IndependentTaskStartResult
{
    private IndependentTaskStartResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public bool Success
    {
        get;
    }

    public string Message
    {
        get;
    }

    public static IndependentTaskStartResult Started(string message)
    {
        return new IndependentTaskStartResult(true, message);
    }

    public static IndependentTaskStartResult Failed(string message)
    {
        return new IndependentTaskStartResult(false, message);
    }
}
