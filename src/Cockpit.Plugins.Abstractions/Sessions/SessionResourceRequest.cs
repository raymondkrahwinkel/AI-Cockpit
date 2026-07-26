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
public sealed record SessionResourceRequest(string PaneId, string? ProjectId);
