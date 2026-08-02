using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.YouTrack;

// Builds the JetBrains remote MCP-server contribution (#60) for each fully-configured YouTrack instance, so
// `YouTrackPlugin.Initialize` can hand each one to `host.AddMcpServer` and give sessions
// (the local tool-loop and the Claude fan-out) YouTrack tools scoped to that instance. Pulled out of
// `YouTrackPlugin` so the pure per-instance mapping — endpoint derivation, skipping an
// incomplete instance — is unit-testable directly, without the plugin-ALC/type-identity ceremony the
// end-to-end loader test needs.
internal static class YouTrackMcpRegistration
{
    // One contribution per `instances` entry that opts in (`YouTrackInstance.AddMcpToSessions`,
    // AC-11) and has both a URL and a token set — an instance still being filled in (either field blank)
    // contributes nothing rather than a server that could never connect, and one with MCP turned off contributes
    // nothing by choice. Named via `ServerName` so multiple instances stay distinct.
    public static IReadOnlyList<McpServerContribution> BuildContributions(IReadOnlyList<YouTrackInstance> instances) =>
        instances
            .Where(instance => instance.AddMcpToSessions
                && !string.IsNullOrWhiteSpace(instance.InstanceUrl)
                && !string.IsNullOrWhiteSpace(instance.Token))
            .Select(instance => new McpServerContribution(
                Name: ServerName(instance.Label),
                Url: DeriveMcpEndpoint(instance.InstanceUrl),
                BearerToken: instance.Token))
            .ToList();

    // The registry names this plugin owns, for every instance regardless of opt-in or completeness (AC-11): what
    // an earlier version pushed into the shared registry, so the plugin can reclaim those entries on load and
    // take their management over itself. Derived from the label the same way `BuildContributions`
    // names them.
    public static IReadOnlyList<string> ManagedServerNames(IReadOnlyList<YouTrackInstance> instances) =>
        instances.Select(instance => ServerName(instance.Label)).ToList();

    // The registry key / display name for an instance's MCP server, e.g. `"YouTrack: Personal"`.
    internal static string ServerName(string label) => $"YouTrack: {label}";

    // The JetBrains remote MCP endpoint, derived from the instance's REST API base URL: drop a trailing
    // "/api" (case-insensitive, tolerant of a trailing slash) and append "/mcp" — e.g.
    // "https://x.youtrack.cloud/api" -&gt; "https://x.youtrack.cloud/mcp". An instance URL with no "/api"
    // suffix (already the site root) just gets "/mcp" appended. Mirrors
    // `YouTrackClient.BuildIssueUrl`'s own "/api"-stripping for the issue web URL.
    internal static string DeriveMcpEndpoint(string instanceBaseUrl)
    {
        var trimmed = instanceBaseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return $"{trimmed}/mcp";
    }
}
