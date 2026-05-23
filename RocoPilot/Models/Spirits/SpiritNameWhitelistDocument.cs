namespace RocoPilot.Models.Spirits;

public sealed class SpiritNameWhitelistDocument
{
    public List<SpiritNameWhitelistItem> Spirits { get; set; } = [];
}

public sealed class SpiritNameWhitelistItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string RecordName { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];
}
