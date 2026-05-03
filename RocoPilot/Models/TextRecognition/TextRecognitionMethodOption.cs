namespace RocoPilot.Models.TextRecognition;

public sealed class TextRecognitionMethodOption
{
    public TextRecognitionMethodOption(
        TextRecognitionMethod method,
        string name,
        string description,
        bool isAvailable,
        string? unavailableReason = null)
    {
        Method = method;
        Name = name;
        Description = description;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
    }

    public TextRecognitionMethod Method
    {
        get;
    }

    public string Name
    {
        get;
    }

    public string Description
    {
        get;
    }

    public bool IsAvailable
    {
        get;
    }

    public string? UnavailableReason
    {
        get;
    }

    public override string ToString() => Name;
}
