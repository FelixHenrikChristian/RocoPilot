namespace RocoPilot.Models.Recognition;

public sealed class RecognitionRegion
{
    public string Id
    {
        get;
        set;
    } = string.Empty;

    public string Name
    {
        get;
        set;
    } = string.Empty;

    public RecognitionRegionPurpose Purpose
    {
        get;
        set;
    }

    public RecognitionRegionShape Shape
    {
        get;
        set;
    } = RecognitionRegionShape.Rectangle;

    public RecognitionRegionBounds Bounds
    {
        get;
        set;
    } = new();

    public bool Enabled
    {
        get;
        set;
    } = true;

    public string? Description
    {
        get;
        set;
    }
}
