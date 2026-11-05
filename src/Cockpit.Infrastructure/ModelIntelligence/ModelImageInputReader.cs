using Cockpit.Core.Abstractions;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.ModelIntelligence;

internal sealed class ModelImageInputReader(
    IModelIntelligence publicData,
    IProviderModelIntelligence providers,
    IPluginProviderRegistry registry) : IModelImageInput, ISingletonService
{
    public bool HasTransport(SessionProfile profile) => profile.Provider is SessionProvider.Ollama or SessionProvider.LmStudio
        || profile.ProviderConfig is PluginProviderConfig plugin && registry.Resolve(plugin.ProviderId)?.Capabilities.SupportsVision == true;

    public async Task<(ImageInputFact? Service, ImageInputFact? OpenRouter)> ReadAsync(SessionProfile profile, string? model, CancellationToken cancellationToken = default)
    {
        PluginModelIntelligence? reading = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            var ownRead = profile.ProviderConfig is PluginProviderConfig plugin ? registry.Resolve(plugin.ProviderId)?.ReadModelIntelligenceAsync : null;
            reading = ownRead is not null && profile.ProviderConfig is PluginProviderConfig config
                ? await ownRead(config.ConfigJson, budget.Token).WaitAsync(budget.Token)
                : await providers.ReadAsync(profile.Label, budget.Token).WaitAsync(budget.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            reading = null;
        }

        IReadOnlyDictionary<string, ModelSnapshot> snapshots = new Dictionary<string, ModelSnapshot>();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            snapshots = (await publicData.GetSnapshotsAsync(budget.Token).WaitAsync(budget.Token)).ToDictionary(value => value.Id, StringComparer.Ordinal);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            snapshots = new Dictionary<string, ModelSnapshot>();
        }

        var estimate = reading?.Models.FirstOrDefault(value => value.Model == model);
        var snapshot = ModelPictureReader.Match(estimate?.PublicModelRef ?? model, snapshots).Snapshot;
        if (estimate is null && snapshot is not null)
        {
            estimate = reading?.Models.FirstOrDefault(value =>
                ModelPictureReader.Match(value.PublicModelRef, snapshots).Snapshot is { } candidate
                && (candidate.AliasTarget ?? candidate.Id) == (snapshot.AliasTarget ?? snapshot.Id));
        }
        var source = estimate?.ImageInputSource ?? reading?.ImageInputSource;
        ImageInputFact? service = source is null ? null : new(estimate?.SupportsImageInput ?? reading?.SupportsImageInput, source.Name, source.AsOf, source.ValidFor);
        ImageInputFact? catalog = snapshot is null ? null : new(snapshot.SupportsImageInput, snapshot.Source, snapshot.FetchedAt, snapshot.TimeToLive);
        return (service, catalog);
    }
}
