namespace RocoPilot.Models.Capture;

public sealed class CaptureMethodOption
{
    public CaptureMethodOption(CaptureMethod method, string name, string description)
    {
        Method = method;
        Name = name;
        Description = description;
    }

    public CaptureMethod Method
    {
        get;
        set;
    }

    public string Name
    {
        get;
        set;
    }

    public string Description
    {
        get;
        set;
    }

    public override string ToString() => Name;
}
