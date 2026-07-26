using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Asks every plugin what it gives a session that is starting and returns the merged, scrubbed result (AC-165).
/// Lives here rather than in the launch routes because it needs the plugin set and the project a pane belongs to,
/// and those live in the app — the same split <see cref="Mcp.IMcpServerCatalog"/> already uses: the contract in
/// Core so Infrastructure can depend on it, the implementation where the plugins are.
/// </summary>
public interface ISessionResourceResolver
{
    /// <summary>
    /// What the plugins contribute to the session starting in <paramref name="paneId"/>, or
    /// <see cref="SessionResources.Empty"/> when none of them has anything for it — including for a pane that has
    /// no project, which is an ordinary session rather than a missing one.
    /// <para>
    /// Never throws: a plugin that fails is left out and logged. A session that starts without an endpoint can be
    /// fixed afterwards; one that refuses to start because a plugin misbehaved cannot.
    /// </para>
    /// </summary>
    Task<SessionResources> ResolveAsync(string? paneId, CancellationToken cancellationToken = default);
}
