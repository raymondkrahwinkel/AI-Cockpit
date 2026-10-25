namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Which session is about to start, as much of it as a plugin needs to answer
/// <see cref="ISessionResourceProvider.GetSessionResourcesAsync"/> (AC-165). Asked once per launch, before the
/// process exists — so it names the session rather than handing one over.
/// </summary>
/// <param name="PaneId">
/// The pane this session is starting in. Stable for the session's life and the same id
/// <see cref="IPluginSessionContext.PaneId"/> reports, so a plugin can key per-session state on it.
/// </param>
/// <param name="ProjectId">
/// The project the session is starting under, or <see langword="null"/> for one started without a project — which
/// is how the cockpit has always started a session, not an error. A plugin that only has something to give a
/// project's sessions returns <see cref="SessionResourceContribution.None"/> here.
/// </param>
public sealed record SessionResourceRequest(string PaneId, string? ProjectId)
{
    /// <summary>
    /// The MCP servers this session will mount, as the launch resolved them, or <see langword="null"/> when the
    /// host could not say — a plugin that only matters to sessions holding a given server treats that as "no".
    /// </summary>
    public IReadOnlyList<SessionResourceMcpServer>? McpServers { get; init; }
}

/// <summary>One MCP server a starting session mounts: its name and, for a stdio server, how it is launched.</summary>
public sealed record SessionResourceMcpServer(string Name, string? Command, IReadOnlyList<string> Args);
