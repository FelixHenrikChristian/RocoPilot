using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;

namespace RocoPilot.Contracts.Services.ImageMatching;

public interface IImageMatchingService
{
    ImageMatchAlgorithm DefaultAlgorithm
    {
        get;
    }

    string TemplateDirectory
    {
        get;
    }

    IReadOnlyList<string> ListTemplatePaths();

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SetDefaultAlgorithmAsync(
        ImageMatchAlgorithm algorithm,
        CancellationToken cancellationToken = default);

    Task<ImageMatchResult> MatchAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        string templatePath,
        ImageMatchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<ImageMatchCollectionResult> FindMatchesAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        string templatePath,
        int maximumMatches,
        ImageMatchOptions? options = null,
        double maximumOverlapRatio = 0.5,
        CancellationToken cancellationToken = default);
}
