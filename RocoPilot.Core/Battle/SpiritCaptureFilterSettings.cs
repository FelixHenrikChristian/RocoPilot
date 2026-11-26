using Newtonsoft.Json;

namespace RocoPilot.Core.Battle;

public enum SpiritCaptureListMode { Off, Blacklist, Whitelist }

public sealed class SpiritCaptureChain
{
    public string Id { get; set; } = string.Empty;
    public List<string> Names { get; set; } = [];
}

public sealed class SpiritCaptureFilterSettings
{
    public SpiritCaptureListMode Mode { get; set; }
    public List<SpiritCaptureChain> Blacklist { get; set; } = [];
    public List<SpiritCaptureChain> Whitelist { get; set; } = [];
    public bool AllowUnrecognizedName { get; set; } = true;

    [JsonIgnore]
    public IReadOnlyList<SpiritCaptureChain> ActiveChains => Mode switch
    {
        SpiritCaptureListMode.Blacklist => Blacklist,
        SpiritCaptureListMode.Whitelist => Whitelist,
        _ => []
    };

    public string GetExclusionReason(string name)
    {
        if (Mode == SpiritCaptureListMode.Off) return string.Empty;
        if (ActiveChains.Count == 0)
            return Mode == SpiritCaptureListMode.Whitelist ? "精灵白名单为空" : string.Empty;
        if (string.IsNullOrEmpty(name))
            return AllowUnrecognizedName ? string.Empty : "未识别精灵名";

        var listed = ActiveChains.Any(chain => chain.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
        return Mode switch
        {
            SpiritCaptureListMode.Blacklist when listed => $"{name}在黑名单内",
            SpiritCaptureListMode.Whitelist when !listed => $"{name}不在白名单内",
            _ => string.Empty
        };
    }

    public SpiritCaptureFilterSettings Clone() => new()
    {
        Mode = Enum.IsDefined(Mode) ? Mode : SpiritCaptureListMode.Off,
        AllowUnrecognizedName = AllowUnrecognizedName,
        Blacklist = CloneChains(Blacklist),
        Whitelist = CloneChains(Whitelist)
    };

    private static List<SpiritCaptureChain> CloneChains(List<SpiritCaptureChain> chains) =>
        (chains ?? []).OfType<SpiritCaptureChain>().Select(chain => new SpiritCaptureChain
        {
            Id = chain.Id,
            Names = (chain.Names ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        }).Where(chain => chain.Names.Count > 0).ToList();
}
