using System.Text.Json.Serialization;

namespace RocoPilot.Models.Recognition;

public sealed class RecognitionRegion
{
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
