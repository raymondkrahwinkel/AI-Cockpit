using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Builds the JetBrains remote MCP-server contribution (#60) for each fully-configured YouTrack instance, so
/// <see cref="YouTrackPlugin.Initialize"/> can hand each one to <c>host.AddMcpServer</c> and give sessions
/// (the local tool-loop and the Claude fan-out) YouTrack tools scoped to that instance. Pulled out of
/// <see cref="YouTrackPlugin"/> so the pure per-instance mapping — endpoint derivation, skipping an
/// incomplete instance — is unit-testable directly, without the plugin-ALC/type-identity ceremony the
/// end-to-end loader test needs.
/// </summary>
internal static class YouTrackMcpRegistration
{
    /// <summary>
    /// One contribution per <paramref name="instances"/> entry that has both a URL and a token set — an
    /// instance still being filled in (either field blank) contributes nothing rather than registering a
    /// server that could never connect. Named <c>"YouTrack: {Label}"</c> so multiple instances register
    /// distinct registry entries instead of colliding on upsert-by-name.
    /// </summary>
    public static IReadOnlyList<McpServerContribution> BuildContributions(IReadOnlyList<YouTrackInstance> instances) =>
        instances
            .Where(instance => !string.IsNullOrWhiteSpace(instance.InstanceUrl) && !string.IsNullOrWhiteSpace(instance.Token))
            .Select(instance => new McpServerContribution(
                Name: $"YouTrack: {instance.Label}",
                Url: DeriveMcpEndpoint(instance.InstanceUrl),
                BearerToken: instance.Token))
            .ToList();

    /// <summary>
    /// The JetBrains remote MCP endpoint, derived from the instance's REST API base URL: drop a trailing
    /// "/api" (case-insensitive, tolerant of a trailing slash) and append "/mcp" — e.g.
    /// "https://x.youtrack.cloud/api" -&gt; "https://x.youtrack.cloud/mcp". An instance URL with no "/api"
    /// suffix (already the site root) just gets "/mcp" appended. Mirrors
    /// <see cref="YouTrackClient.BuildIssueUrl"/>'s own "/api"-stripping for the issue web URL.
    /// </summary>
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
