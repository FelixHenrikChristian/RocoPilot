namespace RocoPilot.Models.ImageMatching;

public enum ImageMatchAlgorithm
{
    OpenCvSqDiffNormalized
}

public sealed class ImageMatchOptions
{
    // 在预计尺寸的 ±4% 内检查五个尺度；设为 0 可用于精确尺寸诊断。
    public double ScaleTolerance { get; set; } = .04;

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

    public double TemplateScaleX
    {
        get;
        set;
    } = 1;

    public double TemplateScaleY
    {
        get;
        set;
    } = 1;
}
