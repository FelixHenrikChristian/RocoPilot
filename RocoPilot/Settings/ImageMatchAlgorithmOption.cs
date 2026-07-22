using RocoPilot.Models.ImageMatching;

namespace RocoPilot.Settings;

public sealed class ImageMatchAlgorithmOption
{
    public required ImageMatchAlgorithm Algorithm
    {
        get;
        init;
    }

    public required string Name
    {
        get;
        init;
    }
}
