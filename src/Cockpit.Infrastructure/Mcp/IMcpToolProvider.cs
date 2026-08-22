using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Connects to the enabled MCP servers in the shared registry (#26) and exposes their tools for a local
/// session's agentic tool-loop. A server that fails to start or is unreachable is skipped so the session
/// still runs with whatever connected.
/// </summary>
internal interface IMcpToolProvider
{
    /// <summary>
    /// <paramref name="enabledServerNames"/> (#44): non-null restricts to those registry servers. <paramref name="confineFileToolsToDirectory"/>
    /// (AC-174) re-roots file-capable servers there for an isolated Autopilot step's worktree. <paramref name="projectId"/>
    /// (AC-218) scopes the registry read; <paramref name="workingDirectory"/> (AC-869) only gates cockpit-github-pull-requests' git-repo mount rule.
    /// </summary>
    Task<IMcpToolSession> ConnectAsync(IReadOnlySet<string>? enabledServerNames = null, string? paneId = null, string? confineFileToolsToDirectory = null, string? projectId = null, string? workingDirectory = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects a single named catalog server on its own, to read its tool list for the pre-flight token estimate
    /// (AC-134). Unlike <see cref="ConnectAsync"/> it does NOT merge the built-in local-default servers — a count must estimate only the ticked server — and skips an OAuth server rather than driving sign-in.
    /// Returns null when unknown/disabled/OAuth-gated/unenumerable, so the caller shows "unknown" not a false zero.
    /// </summary>
    Task<IReadOnlyList<AIFunction>?> EnumerateServerToolsAsync(string serverName, string? projectId = null, CancellationToken cancellationToken = default);
}
