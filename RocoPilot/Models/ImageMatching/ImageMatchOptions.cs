namespace RocoPilot.Models.ImageMatching;

public sealed class ImageMatchOptions
{
    public double MinimumScore
    {
        get;
        set;
    } = 0.9;

    public byte AlphaThreshold
    {
        get;
        set;
    } = 16;

    public int SearchStep
    {
        get;
        set;
    } = 1;
}
