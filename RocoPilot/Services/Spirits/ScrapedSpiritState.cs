using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal sealed class ScrapedSpiritState
{
    public ScrapedSpiritState(
        SpiritCatalogItem item,
        int sourceIndex)
    {
        Item = item;
        SourceIndex = sourceIndex;
    }

    public SpiritCatalogItem Item { get; }

    public int SourceIndex { get; }

    public bool IsPrimaryForm { get; init; }

    public int StageRank { get; set; }
}
