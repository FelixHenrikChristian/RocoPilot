using System.Text.Json.Serialization;

namespace RocoPilot.Services.Spirits;

internal sealed class LcxPokemonDto
{
    [JsonPropertyName("t_id")]
    public string? CatalogId { get; set; }

    public string? Name { get; set; }

    public string? Attributes { get; set; }

    [JsonPropertyName("form_type")]
    public string? FormType { get; set; }

    [JsonPropertyName("chain_group")]
    public string? ChainGroup { get; set; }

    [JsonPropertyName("evolution_stage")]
    public string? EvolutionStage { get; set; }

    [JsonPropertyName("form_id")]
    public string? FormId { get; set; }

    [JsonPropertyName("form_name")]
    public string? FormName { get; set; }

    [JsonPropertyName("form_display_name")]
    public string? FormDisplayName { get; set; }

    [JsonPropertyName("is_form")]
    public bool IsForm { get; set; }

    [JsonPropertyName("form_image_path")]
    public string? FormImagePath { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}
