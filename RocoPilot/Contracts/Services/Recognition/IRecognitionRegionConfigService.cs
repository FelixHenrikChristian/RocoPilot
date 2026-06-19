using RocoPilot.Models.Recognition;

namespace RocoPilot.Contracts.Services.Recognition;

public interface IRecognitionRegionConfigService
{
    IReadOnlyList<string> ListConfigPaths();

    bool TryResolveConfigResolution(
        int width,
        int height,
        out int configWidth,
        out int configHeight);

    RecognitionRegionConfig LoadForResolution(int width, int height);

    RecognitionRegionConfig LoadFromPath(string path);

    void Save(RecognitionRegionConfig config);

    string GetConfigPath(int width, int height);
}
