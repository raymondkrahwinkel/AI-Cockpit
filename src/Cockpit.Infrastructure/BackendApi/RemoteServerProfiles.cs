using System.Net;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1473: a server's profiles over its backend API. A change goes out as the fields it names, never as the list read
// back, which holds no secrets and would wipe them on the server.
internal sealed class RemoteServerProfiles(BackendApiClient client) : IServerProfiles
{
    public async Task<IReadOnlyList<RemoteProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        [.. (await _CallAsync<RemoteProfileList>(HttpMethod.Get, "api/v1/profiles", null, cancellationToken).ConfigureAwait(false)).Profiles.Select(_Read)];

    public async Task<IReadOnlyList<string>> ListProvidersAsync(CancellationToken cancellationToken = default) =>
        (await _CallAsync<RemoteProfileList>(HttpMethod.Get, "api/v1/profiles", null, cancellationToken).ConfigureAwait(false)).Providers ?? [];

    public Task<RemoteProfile> CreateAsync(RemoteNewProfile profile, CancellationToken cancellationToken = default) =>
        _CallAsync<RemoteProfile>(HttpMethod.Post, "api/v1/profiles", profile, cancellationToken);

    public async Task<RemoteProfile?> UpdateAsync(string label, RemoteProfilePatch patch, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _CallAsync<RemoteProfile>(HttpMethod.Patch, _Path(label), patch, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string label, CancellationToken cancellationToken = default)
    {
        try
        {
            await _CallAsync<JsonObject>(HttpMethod.Delete, _Path(label), null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // AC-1585: the whole profile, made or put in place of `originalLabel`; the server keeps each masked secret.
    public async Task<RemoteProfile> SaveProfileAsync(string? originalLabel, SessionProfile profile, CancellationToken cancellationToken = default)
    {
        var patch = new RemoteProfilePatch { Profile = RemoteProfileWire.ToJson(profile) };
        var saved = originalLabel is null
            ? await _CallAsync<RemoteProfile>(HttpMethod.Post, "api/v1/profiles", new RemoteNewProfile(profile.Label, _ProviderId(profile), patch), cancellationToken).ConfigureAwait(false)
            : await _CallAsync<RemoteProfile>(HttpMethod.Patch, _Path(originalLabel), patch, cancellationToken).ConfigureAwait(false);
        return _Read(saved);
    }

    public Task<IReadOnlyList<RemoteProviderInfo>> ListProviderInfoAsync(CancellationToken cancellationToken = default) =>
        _CallAsync<IReadOnlyList<RemoteProviderInfo>>(HttpMethod.Get, "api/v1/profiles/providers", null, cancellationToken);

    public Task<IReadOnlyList<string>> ListMcpServersAsync(CancellationToken cancellationToken = default) =>
        _CallAsync<IReadOnlyList<string>>(HttpMethod.Get, "api/v1/profiles/mcp-servers", null, cancellationToken);

    public Task<McpServerToolEstimate> EstimateMcpToolsAsync(string name, bool refresh, CancellationToken cancellationToken = default) =>
        _CallAsync<McpServerToolEstimate>(HttpMethod.Get, $"api/v1/profiles/mcp-servers/{Uri.EscapeDataString(name)}/estimate?refresh={(refresh ? "true" : "false")}", null, cancellationToken);

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, string? label, CancellationToken cancellationToken = default) =>
        _CallAsync<IReadOnlyList<string>>(HttpMethod.Post, "api/v1/profiles/models", new { baseUrl, apiKey, label }, cancellationToken);

    private static RemoteProfile _Read(RemoteProfile profile) =>
        profile.Profile is { } json ? profile with { Full = RemoteProfileWire.FromJson(json) } : profile;

    private static string _ProviderId(SessionProfile profile) => profile.ProviderConfig is PluginProviderConfig plugin
        ? plugin.ProviderId
        : profile.Provider.ToString().ToLowerInvariant();

    private static string _Path(string label) => $"api/v1/profiles/{Uri.EscapeDataString(label)}";

    private async Task<T> _CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync<T>(method, path, body, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            throw new ArgumentException(exception.Description, exception);
        }
    }

    private sealed record RemoteProfileList(IReadOnlyList<RemoteProfile> Profiles, IReadOnlyList<string>? Providers = null);
}
