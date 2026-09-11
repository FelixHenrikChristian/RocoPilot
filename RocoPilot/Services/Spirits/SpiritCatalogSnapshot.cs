using System.Text.Json;
using RocoPilot.Models.Spirits;

namespace RocoPilot.Services.Spirits;

/// <summary>同一版本的文档与查询索引一次发布，对外只提供独立文档副本。</summary>
internal sealed class SpiritCatalogSnapshot
{
    private readonly string _json;
    public SpiritCatalogIndex Index { get; }

    public SpiritCatalogSnapshot(SpiritCatalogDocument document)
    {
        _json = JsonSerializer.Serialize(document);
        Index = new SpiritCatalogIndex(document);
    }

    public SpiritCatalogDocument CreateDocument() => JsonSerializer.Deserialize<SpiritCatalogDocument>(_json)!;
}
