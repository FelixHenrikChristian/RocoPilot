using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;

namespace RocoPilot.Contracts.Services.ImageMatching;

public interface IImageMatchingService
{
    string TemplateDirectory
    {
        get;
    }

    IReadOnlyList<string> ListTemplatePaths();

    Task<ImageMatchResult> MatchAsync(
        CapturedFrame frame,
        RecognitionRegion region,
        string templatePath,
        ImageMatchOptions? options = null,
        CancellationToken cancellationToken = default);
}
