using RocoPilot.Models.Recognition;

namespace RocoPilot.Contracts.Services.Recognition;

public interface IRecognitionRegionConfigService
{
    RecognitionRegionConfig LoadForResolution(int width, int height);

    string GetConfigPath(int width, int height);
}
