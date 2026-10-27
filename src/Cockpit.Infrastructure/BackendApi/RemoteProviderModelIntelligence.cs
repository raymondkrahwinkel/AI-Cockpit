using System.Net;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1808: the same read on a connect server, whose plugins run the profile. A server without the route reads as unknown.
public sealed class RemoteProviderModelIntelligence(BackendApiClient client) : IProviderModelIntelligence
{
    public async Task<PluginModelIntelligence?> ReadAsync(string profileLabel, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.SendAsync<PluginModelIntelligence?>(
                HttpMethod.Get,
                $"api/v1/profiles/{Uri.EscapeDataString(profileLabel)}/model-intelligence",
                null,
                ConnectKeyEndpoints.Json,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
