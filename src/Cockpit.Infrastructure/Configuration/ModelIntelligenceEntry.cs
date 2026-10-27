namespace Cockpit.Infrastructure.Configuration;

// On-disk operator data for models no public source can identify, in the `modelIntelligence` section of cockpit.json.
internal sealed class ModelIntelligenceEntry
{
    public List<LocalModelEntry> LocalModels { get; set; } = [];
}

internal sealed class LocalModelEntry
{
    public string ModelId { get; set; } = string.Empty;

    public string? Tier { get; set; }

    public List<string> Strengths { get; set; } = [];

    public string? Notes { get; set; }
}
