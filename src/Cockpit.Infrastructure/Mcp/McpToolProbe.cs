using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// `IMcpToolProbe` — one tool call against an already-configured MCP server, outside any running
// session (AC-503). A caller (a plugin, through `ICockpitHost.ProbeMcpToolAsync`) uses this to confirm a value
// the operator typed actually resolves to something, without a session to ask through.
internal sealed class McpToolProbe(
    IMcpServerStore store,
    IMcpOAuthCoordinator oauthCoordinator,
    IMcpOAuthAuthorizer oauthAuthorizer,
    McpAuthKey authKey,
    ILogger<McpToolProbe> logger)
    : IMcpToolProbe, ISingletonService
{
    // How long the connect-and-call together may take. Deliberately a few seconds, nowhere near the multi-minute
    // allowance `McpInteractiveOAuthClientOptions` gives an interactive sign-in (AC-505 follow-up) —
    // this call must never open a browser at all (see `ProbeAsync`'s own remarks), so there is nothing
    // here for a long timeout to wait out. Long enough for an ordinary connect-plus-one-tool-call over a live
    // network, short enough that a project editor waiting on this does not sit for anywhere near as long as the
    // old per-row filesystem probe's own 200 ms budget would suggest is "a while" for a check like this.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    public async Task<McpToolProbeResult> ProbeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        IReadOnlyList<McpServerConfig>? callerFallbackServers = null,
        CancellationToken cancellationToken = default)
    {
        McpServerConfig? server;
        try
        {
            server = (await store.LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(candidate.Name, serverName, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Reading the MCP server registry to probe {Server} failed.", serverName);
            return McpToolProbeResult.Failed;
        }

        // AC-499: this call has no project id to resolve a plugin-delivered server through — a plugin whose servers
        // never land in the registry (Depot, AC-504) would otherwise be unprobeable no matter what. The host scopes
        // this list to the calling plugin's own contributions before it ever reaches here (see
        // ICockpitHost.ProbeMcpToolAsync's own remarks), so this is not a broadening of what an arbitrary caller can
        // reach — only what this specific caller was already entitled to.
        server ??= callerFallbackServers?.FirstOrDefault(candidate => string.Equals(candidate.Name, serverName, StringComparison.Ordinal));

        // Unknown to the registry and to the caller's own fallback: not this call's to guess at, and not a claim
        // about the value being checked — see IMcpToolProbe.ProbeAsync's own remarks on why this is Failed rather
        // than NotFound.
        if (server is null)
        {
            return McpToolProbeResult.Failed;
        }

        // Ask non-interactively first, the same restraint GetMcpServerAuthStateAsync already takes: a server that
        // needs a sign-in gets no connection attempt at all, and — critically — never a browser. GetStateAsync is a
        // local read (no network), so this costs nothing when the server is not OAuth-protected at all.
        if (server.Auth == McpServerAuth.OAuth
            && await oauthCoordinator.GetStateAsync(server, cancellationToken).ConfigureAwait(false) == McpAuthState.AuthorizationRequired)
        {
            return McpToolProbeResult.NotSignedIn;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            var transport = _BuildTransport(server);
            // interactive: false — this connection must never open a browser (see ProbeAsync's own doc comment).
            // A server whose token has just gone stale between the GetStateAsync read above and this connect
            // renews silently if it can (the SDK's own refresh grant), same as any other non-interactive use.
            var clientOptions = new McpClientOptions { InitializationTimeout = Budget, DiscoverProbeTimeout = Budget };
            await using var client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: budget.Token).ConfigureAwait(false);

            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: budget.Token).ConfigureAwait(false);
            return _ToResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token, not this method's internal budget — respect it rather than reporting a
            // result for a call the caller itself gave up on.
            throw;
        }
        catch (Exception exception)
        {
            // A timeout (this method's own budget expiring), a network failure, an auth handshake that fails after
            // all — none of these are evidence the value does not exist, only that nothing could be confirmed.
            // Iron Law #8: never log anything from server/arguments that could carry a credential — a server name
            // and tool name are configuration, not secrets.
            logger.LogInformation(exception, "Probing MCP server {Server}'s tool {Tool} could not be confirmed.", serverName, toolName);
            return McpToolProbeResult.Failed;
        }
    }

    // Reads a recognisable "not found" out of an error result — deliberately narrow. The MCP spec lets a tool
    // report its own failures inside `CallToolResult.IsError` rather than as a protocol-level
    // exception, but nothing here can verify what any given server's error text actually means. Only a plainly
    // legible phrase (case-insensitive) is read as NotFound; anything else that came back as an error is Failed —
    // an honest "could not confirm" rather than a guess dressed up as a specific answer.
    private static McpToolProbeResult _ToResult(CallToolResult result)
    {
        var text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        if (result.IsError != true)
        {
            return McpToolProbeResult.Success(text.Length > 0 ? text : null);
        }

        return text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such", StringComparison.OrdinalIgnoreCase)
            || text.Contains("404", StringComparison.Ordinal)
            ? McpToolProbeResult.NotFound
            : McpToolProbeResult.Failed;
    }

    // Deliberately its own minimal transport, mirroring McpToolProvider's _BuildTransport but without the in-process
    // tool-loop's session-token/confinement concerns, which have no meaning for a single out-of-session call. OAuth
    // goes through the authorizer non-interactively — see ProbeAsync's own remarks on why this may never pop a browser.
    private IClientTransport _BuildTransport(McpServerConfig server) => server.Transport switch
    {
        McpTransport.Stdio => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command ?? string.Empty,
            Arguments = [.. server.Args],
            EnvironmentVariables = StdioServerEnvironment.Build(),
        }),
        // AC-792: the same certificate pin the session route applies, for the same server — a probe that trusted
        // more than the session does would report a node as reachable that no session can reach.
        McpTransport.Http => NodeCertificatePin.TransportFor(server, new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? string.Empty),
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = new Dictionary<string, string>(McpAgentHeaders.For(server, CockpitMcpBearer.For(server, authKey))),
            OAuth = server.Auth == McpServerAuth.OAuth ? oauthAuthorizer.CreateOptions(server, interactive: false) : null,
        }),
        _ => throw new NotSupportedException($"Unsupported MCP transport {server.Transport}."),
    };
}
