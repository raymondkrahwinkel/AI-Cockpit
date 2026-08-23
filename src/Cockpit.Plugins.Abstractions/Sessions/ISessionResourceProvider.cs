namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Implemented by a plugin that has something to give a session when it starts (AC-165) — a variable the tools in
/// that session need, resolved from the project the session belongs to. The host asks every registered provider,
/// merges the answers, and hands the result to whichever provider is starting the session.
/// </summary>
/// <remarks>
/// Asked rather than pushed, like <see cref="Mcp.IPluginMcpProvider"/>: the plugin answers with whatever it
/// currently holds, so a changed value takes effect on the next session with no separate store to keep in sync.
/// It is not told which session or project it is answering for.
/// </remarks>
public interface ISessionResourceProvider
{
    /// <summary>
    /// What this plugin gives the session described by <paramref name="request"/>, or
    /// <see cref="SessionResourceContribution.None"/> when it has nothing to offer — the answer for a session
    /// whose project isn't linked to anything of this plugin's.
    /// </summary>
    /// <remarks>
    /// Runs while the operator waits for the session to open, so keep it fast: read what the plugin already holds
    /// rather than reaching for the network. A call that throws is logged and treated as
    /// <see cref="SessionResourceContribution.None"/> — one plugin's failure never blocks a session from starting.
    /// </remarks>
    Task<SessionResourceContribution> GetSessionResourcesAsync(SessionResourceRequest request, CancellationToken cancellationToken = default);
}
