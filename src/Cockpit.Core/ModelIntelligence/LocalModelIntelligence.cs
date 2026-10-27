using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.ModelIntelligence;

// Operator-supplied information for a model installed in LM Studio.
public sealed record LocalModelEntry(string ModelId, string? Tier, IReadOnlyList<string> Strengths, string? Notes);

// Facts Cockpit knows about a locally hosted model.
public sealed record LocalModelFacts(
    string ModelId,
    ModelPrice Price,
    IReadOnlyList<LocalModelQuota> Quota,
    string? Tier,
    IReadOnlyList<string> Strengths,
    string? Notes);

// LM Studio has no provider quota to report.
public sealed record LocalModelQuota;

// Reads installed LM Studio models and overlays only the operator's exact local-table row.
public sealed class LocalModelIntelligence(IModelCatalog catalog)
{
    public async Task<IReadOnlyList<LocalModelFacts>> GetLmStudioModelsAsync(
        string baseUrl,
        string? apiKey,
        IReadOnlyList<LocalModelEntry> localModels,
        CancellationToken cancellationToken = default)
    {
        var entries = localModels.ToDictionary(entry => entry.ModelId, StringComparer.Ordinal);
        var modelIds = await catalog.ListModelsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        return modelIds.Select(modelId =>
        {
            entries.TryGetValue(modelId, out var entry);
            return new LocalModelFacts(
                modelId,
                new ModelPrice(0m, 0m),
                [],
                entry?.Tier,
                entry?.Strengths ?? [],
                entry?.Notes);
        }).ToArray();
    }
}
