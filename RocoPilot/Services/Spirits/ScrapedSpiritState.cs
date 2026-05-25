using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

internal sealed class ScrapedSpiritState
{
    public ScrapedSpiritState(
        SpiritCatalogItem item,
        int sourceIndex,
        string listStage,
        string listPrimaryAttribute,
        string listSecondaryAttribute,
        string listForm,
        string listHasShiny)
    {
        Item = item;
        SourceIndex = sourceIndex;
        ListStage = listStage;
        ListPrimaryAttribute = listPrimaryAttribute;
        ListSecondaryAttribute = listSecondaryAttribute;
        ListForm = listForm;
        ListHasShiny = listHasShiny;
    }

    public SpiritCatalogItem Item { get; }

    public int SourceIndex { get; }

    public string ListStage { get; }

    public string ListPrimaryAttribute { get; }

    public string ListSecondaryAttribute { get; }

    public string ListForm { get; }

    public string ListHasShiny { get; }

    public int StageRank { get; set; }
}
