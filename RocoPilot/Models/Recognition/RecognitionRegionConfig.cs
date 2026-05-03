using System.Text.Json.Serialization;

namespace RocoPilot.Models.Recognition;

public sealed class RecognitionRegionConfig
{
    public int ResolutionWidth
    {
        get;
        set;
    }

    public int ResolutionHeight
    {
        get;
        set;
    }

    public List<RecognitionRegion> Regions
    {
        get;
        set;
    } = [];

    [JsonIgnore]
    public string SourcePath
    {
        get;
        set;
    } = string.Empty;

    [JsonIgnore]
    public bool LoadedFromFile
    {
        get;
        set;
    }

    public static RecognitionRegionConfig Empty(int width, int height, string sourcePath)
    {
        return new RecognitionRegionConfig
        {
            ResolutionWidth = width,
            ResolutionHeight = height,
            SourcePath = sourcePath,
            LoadedFromFile = false
        };
    }
}
