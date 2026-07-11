using RocoPilot.Models.Capture;
using RocoPilot.Models.Statistics;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsUidDetectionService
{
    Task<StatisticsUidDetectionResult> DetectAsync(
        CaptureMethod preferredCaptureMethod,
        TextRecognitionMethod textRecognitionMethod,
        CancellationToken cancellationToken = default);
}
