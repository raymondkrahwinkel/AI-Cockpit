using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Browser;

// AC-1712: Hands a session that mounts Playwright MCP its own browser, at launch, on the one seam both routes share
// (AC-294): the environment a plugin contributes (AC-165). Playwright MCP reads PLAYWRIGHT_MCP_CDP_ENDPOINT as its
// `--cdp-endpoint`. A session without Playwright MCP, or a host that did not say what it mounts, is left alone.
internal sealed class AgentBrowserSessionResources(AgentBrowserHost browsers, Func<bool> enabled) : ISessionResourceProvider
{
    internal const string EndpointVariable = "PLAYWRIGHT_MCP_CDP_ENDPOINT";

    public async Task<SessionResourceContribution> GetSessionResourcesAsync(SessionResourceRequest request, CancellationToken cancellationToken = default)
    {
        if (!enabled() || request.McpServers is not { } servers || !servers.Any(IsPlaywright))
        {
            return SessionResourceContribution.None;
        }

        if (await browsers.StartAsync(request.PaneId, cancellationToken).ConfigureAwait(false) is not { } endpoint)
        {
            return SessionResourceContribution.None;
        }

        return new SessionResourceContribution
        {
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal) { [EndpointVariable] = endpoint },
        };
    }

    // By how it is launched first (`npx @playwright/mcp`, a `playwright-mcp` binary), by name only as the fallback.
    internal static bool IsPlaywright(SessionResourceMcpServer server) =>
        server.Args.Any(argument => argument.StartsWith("@playwright/mcp", StringComparison.OrdinalIgnoreCase))
        || Path.GetFileNameWithoutExtension(server.Command ?? string.Empty).Equals("playwright-mcp", StringComparison.OrdinalIgnoreCase)
        || server.Name.Equals("playwright", StringComparison.OrdinalIgnoreCase);
}
