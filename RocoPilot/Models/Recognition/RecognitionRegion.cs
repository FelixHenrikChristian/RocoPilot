using System.Text.Json.Serialization;

namespace RocoPilot.Models.Recognition;

public enum RecognitionAnchor
{
    Auto, TopLeft, TopCenter, TopRight, CenterLeft, Center, CenterRight, BottomLeft, BottomCenter, BottomRight
}

public sealed class RecognitionRegion
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RecognitionAnchor Anchor { get; set; }

    [JsonIgnore]
    public bool IsMatched { get; set; }

    public string Id
    {
        get;
        set;
    } = string.Empty;

    public int X
    {
        get;
        set;
    }

    public int Y
    {
        get;
        set;
    }

    public int Width
    {
        get;
        set;
    }

    public int Height
    {
        get;
        set;
    }

    public bool Enabled
    {
        get;
        set;
    } = true;
}
