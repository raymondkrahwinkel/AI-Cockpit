using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1808: the read on the machine whose plugins run the profile. A plugin's failure is its own; the caller sees unknown.
internal sealed class PluginModelIntelligenceReader(IPluginProviderRegistry registry, ISessionProfileStore profiles)
    : IProviderModelIntelligence, ISingletonService
{
    public async Task<PluginModelIntelligence?> ReadAsync(string profileLabel, CancellationToken cancellationToken = default)
    {
        var profile = (await profiles.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Label, profileLabel, StringComparison.Ordinal));
        if (profile?.ProviderConfig is not PluginProviderConfig config
            || registry.Resolve(config.ProviderId)?.ReadModelIntelligenceAsync is not { } read)
        {
            return null;
        }

        try
        {
            return await read(config.ConfigJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
