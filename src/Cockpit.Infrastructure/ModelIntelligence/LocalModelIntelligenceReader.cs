using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.ModelIntelligence;

// Joins the operator-owned `cockpit.json` table to ids currently reported by LM Studio.
internal sealed class LocalModelIntelligenceReader
{
    private readonly CockpitConfigFileAccess _configFile;
    private readonly LocalModelIntelligence _intelligence;

    public LocalModelIntelligenceReader(IModelCatalog catalog)
        : this(catalog, CockpitConfigPath.Default)
    {
    }

    internal LocalModelIntelligenceReader(IModelCatalog catalog, string configFilePath)
    {
        _intelligence = new LocalModelIntelligence(catalog);
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<LocalModelFacts>> GetLmStudioModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var localModels = (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.ModelIntelligence?.LocalModels
            .Select(entry => new Cockpit.Core.ModelIntelligence.LocalModelEntry(entry.ModelId, entry.Tier, entry.Strengths, entry.Notes))
            .ToArray() ?? [];
        return await _intelligence.GetLmStudioModelsAsync(baseUrl, apiKey, localModels, cancellationToken).ConfigureAwait(false);
    }
}
