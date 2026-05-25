namespace RocoPilot.Models.Spirits;

public sealed class SpiritCatalogDocument
{
    public SpiritCatalogSource Source { get; set; } = new();

    public int Count { get; set; }

    public List<SpiritCatalogItem> Spirits { get; set; } = [];

    public List<SpiritEvolutionChain> EvolutionChains { get; set; } = [];
}

public sealed class SpiritCatalogSource
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ListUrl { get; set; } = string.Empty;

    public DateTimeOffset ScrapedAt { get; set; }
}

public sealed record SpiritCatalogSourceOption(string Id, string Name, string ListUrl);

public sealed class SpiritCatalogItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string WikiName { get; set; } = string.Empty;

    public string BaseName { get; set; } = string.Empty;

    public string PageUrl { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;

    public string OriginalImageUrl { get; set; } = string.Empty;

    public string AvatarPath { get; set; } = string.Empty;

    public string Stage { get; set; } = string.Empty;

    public string Form { get; set; } = string.Empty;

    public string RegionalForm { get; set; } = string.Empty;

    public bool HasShiny { get; set; }

    public string PrimaryAttribute { get; set; } = string.Empty;

    public string SecondaryAttribute { get; set; } = string.Empty;

    public string UpdateVersion { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string ChainId { get; set; } = string.Empty;

    public string BaseId { get; set; } = string.Empty;

    public string FinalId { get; set; } = string.Empty;

    public string FinalName { get; set; } = string.Empty;

    public List<SpiritEvolutionChainMember> EvolutionChain { get; set; } = [];

    public List<string> EvolutionChainNames { get; set; } = [];
}

public sealed class SpiritEvolutionChain
{
    public string Id { get; set; } = string.Empty;

    public string BaseId { get; set; } = string.Empty;

    public string BaseName { get; set; } = string.Empty;

    public string HighestId { get; set; } = string.Empty;

    public string HighestName { get; set; } = string.Empty;

    public List<SpiritEvolutionChainMember> HighestCandidates { get; set; } = [];

    public List<SpiritEvolutionChainMember> Spirits { get; set; } = [];

    public List<string> Names { get; set; } = [];
}

public sealed class SpiritEvolutionChainMember
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Stage { get; set; } = string.Empty;

    public string Form { get; set; } = string.Empty;

    public string RegionalForm { get; set; } = string.Empty;
}

public sealed record SpiritCatalogSyncProgress(int Completed, int Total, string Message);
