using RocoPilot.Models.Capture;
using RocoPilot.Models.TextRecognition;

namespace RocoPilot.Models.Statistics;

public sealed record StatisticsUidConfirmationRequest(
    string? SuggestedUid,
    string Message,
    CaptureMethod CaptureMethod,
    TextRecognitionMethod TextRecognitionMethod,
    bool RecognitionSucceeded,
    bool WasPresented = false);
