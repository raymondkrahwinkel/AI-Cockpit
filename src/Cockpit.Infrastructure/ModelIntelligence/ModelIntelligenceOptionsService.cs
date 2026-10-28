using Cockpit.Core.ModelIntelligence;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.ModelIntelligence;

// AC-1814: what Options › Model intelligence reads and writes — source ages, the operator's local table and the derived patterns.
internal sealed class ModelIntelligenceOptionsService(
    IModelIntelligence publicData,
    ModelPictureReader picture,
    IModelObservationStore observations,
    CockpitConfigFileAccess configFile) : IModelIntelligenceOptions
{
    private const string OwnMeasurements = "Own measurements";

    public async Task<ModelIntelligenceOverview> ReadAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await publicData.GetSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await picture.ReadAsync(cancellationToken).ConfigureAwait(false)).Rows;
        var patterns = ModelPatterns.Derive(observations.Read(), TimeZoneInfo.Local);

        var sources = snapshots.GroupBy(snapshot => snapshot.Source).Select(group => new ModelDataAge(group.Key, group.Max(snapshot => snapshot.FetchedAt))).ToList();
        if (sources.Count == 0)
        {
            sources.Add(new ModelDataAge("OpenRouter", null));
        }

        // Provider facts carry their own source and age; the public source and our own measurements are listed on their own.
        var provider = rows
            .SelectMany(row => new[] { row.Price?.Source }.Concat(row.PeakWindows.Select(window => window.Source)).Concat(row.Quota.Select(quota => (Sourced?)Sourced.From(quota.Source))).Concat(row.Limits.Select(limit => (Sourced?)Sourced.From(limit.Source))))
            .OfType<Sourced>()
            .Where(source => !sources.Any(known => known.Source == source.Name) && source.ValidFor != TimeSpan.MaxValue)
            .GroupBy(source => source.Name)
            .Select(group => new ModelDataAge(group.Key, group.Max(source => source.AsOf)));
        sources.AddRange(provider);
        sources.Add(new ModelDataAge(OwnMeasurements, patterns.Count == 0 ? null : patterns.Max(pattern => pattern.To)));

        var local = (await configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.ModelIntelligence?.LocalModels
            .Select(entry => new Cockpit.Core.ModelIntelligence.LocalModelEntry(entry.ModelId, entry.Tier, entry.Strengths, entry.Notes))
            .ToArray() ?? [];
        return new ModelIntelligenceOverview(sources, local, patterns);
    }

    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => publicData.RefreshAsync(cancellationToken);

    public Task SaveLocalModelsAsync(IReadOnlyList<Cockpit.Core.ModelIntelligence.LocalModelEntry> localModels, CancellationToken cancellationToken = default) =>
        configFile.UpdateAsync(file =>
        {
            file.ModelIntelligence ??= new ModelIntelligenceEntry();
            file.ModelIntelligence.LocalModels = [.. localModels
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ModelId))
                .Select(entry => new Configuration.LocalModelEntry { ModelId = entry.ModelId.Trim(), Tier = entry.Tier, Strengths = [.. entry.Strengths], Notes = entry.Notes })];
        }, cancellationToken);
}
