using System.Text.Json;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2282: the ordinary MCP Servers page of a remote window, over the server's registry. A secret reads as the server's
// marker and goes back as it came; sign-in runs on the server with its browser step here, so no token reaches this window.
internal sealed class RemoteMcpRegistry(Func<BackendApiClient> client, Func<bool> readConfirmed) : IMcpServerStore, IMcpOAuthCoordinator
{
    private const string Route = "api/v1/mcp/servers";

    private IReadOnlyList<McpServerConfig>? _confirmed;

    public event Action<McpServerConfig>? SignInNeeded { add { } remove { } }

    public event Action<McpServerConfig>? AccountChanged { add { } remove { } }

    public async Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (readConfirmed() && _confirmed is { } confirmed)
        {
            return confirmed;
        }

        return _confirmed = _FromWire(await client().SendAsync<IReadOnlyList<McpServerWire>>(HttpMethod.Get, Route, null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false));
    }

    public async Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default) =>
        _confirmed = _FromWire(await client().SendAsync<IReadOnlyList<McpServerWire>>(
            HttpMethod.Put, Route, new McpServersBody(McpRegistryEndpoints.ToWire(servers, redact: false)), ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false));

    public async Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
        (await client().SendAsync<JsonElement>(HttpMethod.Get, $"{_Path(server)}/auth-state", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false))
            .GetProperty("state").Deserialize<McpAuthState>(ConnectKeyEndpoints.Json);

    public async Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
        await client().SendAsync<JsonElement>(HttpMethod.Post, $"{_Path(server)}/sign-out", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);

    // Only the operator's own Sign in reaches here; a session on the server renews its tokens there.
    public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default)
    {
        if (!interactive)
        {
            throw new NotSupportedException("A remote window holds no MCP tokens.");
        }

        return await new RemotePluginAdministration(client()).SignInMcpAtAsync($"{_Path(server)}/sign-in", server.Name, cancellationToken).ConfigureAwait(false) switch
        {
            "Authorized" => new(McpAuthState.Authorized, null),
            "Declined" => new(McpAuthState.AuthorizationRequired, null, McpSignInStage.BrowserRequested),
            _ => McpOAuthAccess.AuthorizationRequired,
        };
    }

    public Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A remote window holds no MCP tokens.");

    private static string _Path(McpServerConfig server) => $"{Route}/{Uri.EscapeDataString(server.IdentityKey)}";

    private static IReadOnlyList<McpServerConfig> _FromWire(IReadOnlyList<McpServerWire> servers) =>
    [
        .. servers.Select(server => new McpServerConfig
        {
            Id = server.Id, Name = server.Name, Transport = server.Transport, Scope = server.Scope, Command = server.Command,
            Args = server.Args ?? [], Url = server.Url, Auth = server.Auth, ApiKey = server.ApiKey,
            OAuthAuthority = server.OAuthAuthority, OAuthClientId = server.OAuthClientId, OAuthScopes = server.OAuthScopes,
            Headers = server.Headers ?? [], Enabled = server.Enabled,
        }),
    ];
}
