namespace RocoPilot.Models.ImageMatching;

public sealed record ImageMatchCollectionResult(
    IReadOnlyList<ImageMatchResult> Matches,
    double BestScore,
    string TemplatePath)
{
    public static ImageMatchCollectionResult NoMatch(double bestScore, string templatePath)
    {
        return new ImageMatchCollectionResult([], bestScore, templatePath);
    }
}
