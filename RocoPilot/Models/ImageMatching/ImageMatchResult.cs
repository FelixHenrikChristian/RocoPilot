namespace RocoPilot.Models.ImageMatching;

public sealed record ImageMatchResult(
    bool IsMatch,
    double Score,
    int X,
    int Y,
    int Width,
    int Height,
    string TemplatePath)
{
    public static ImageMatchResult NoMatch(double score, string templatePath)
    {
        return new ImageMatchResult(false, score, 0, 0, 0, 0, templatePath);
    }
}
