using Cockpit.Core.Abstractions;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.ModelIntelligence;

internal sealed class ModelImageInputReader(
    IModelIntelligence publicData,
    IProviderModelIntelligence providers,
    IPluginProviderRegistry registry) : IModelImageInput, ISingletonService
{
    public bool HasTransport(SessionProfile profile) => profile.ProviderConfig is PluginProviderConfig plugin
        && registry.Resolve(plugin.ProviderId)?.Capabilities.SupportsVision == true;

    public async Task<(ImageInputFact? Service, ImageInputFact? OpenRouter)> ReadAsync(SessionProfile profile, string? model, CancellationToken cancellationToken = default)
    {
        ImageInputFact? service = null;
        string? reference = model;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            var ownRead = profile.ProviderConfig is PluginProviderConfig plugin ? registry.Resolve(plugin.ProviderId)?.ReadModelIntelligenceAsync : null;
            var reading = ownRead is not null && profile.ProviderConfig is PluginProviderConfig config
                ? await ownRead(config.ConfigJson, budget.Token).WaitAsync(budget.Token)
                : await providers.ReadAsync(profile.Label, budget.Token).WaitAsync(budget.Token);
            var estimate = reading?.Models.FirstOrDefault(value => value.Model == model);
            var source = estimate?.ImageInputSource ?? reading?.ImageInputSource;
            service = source is null ? null : new(estimate?.SupportsImageInput ?? reading?.SupportsImageInput, source.Name, source.AsOf, source.ValidFor);
            reference = estimate?.PublicModelRef ?? model;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            service = null;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            var snapshot = (await publicData.GetSnapshotsAsync(budget.Token).WaitAsync(budget.Token)).FirstOrDefault(value => value.Id == reference);
            ImageInputFact? catalog = snapshot is null ? null : new(snapshot.SupportsImageInput, snapshot.Source, snapshot.FetchedAt, snapshot.TimeToLive);
            return (service, catalog);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (service, null);
        }
    }
}
