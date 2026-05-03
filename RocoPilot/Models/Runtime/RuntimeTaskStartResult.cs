namespace RocoPilot.Models.Runtime;

public sealed class RuntimeTaskStartResult
{
    private RuntimeTaskStartResult(bool success, string message, RuntimeTaskState? state)
    {
        Success = success;
        Message = message;
        State = state;
    }

    public bool Success
    {
        get;
    }

    public string Message
    {
        get;
    }

    public RuntimeTaskState? State
    {
        get;
    }

    public static RuntimeTaskStartResult Started(RuntimeTaskState state)
    {
        return new RuntimeTaskStartResult(true, "任务已启动。", state);
    }

    public static RuntimeTaskStartResult Failed(string message)
    {
        return new RuntimeTaskStartResult(false, message, null);
    }
}
