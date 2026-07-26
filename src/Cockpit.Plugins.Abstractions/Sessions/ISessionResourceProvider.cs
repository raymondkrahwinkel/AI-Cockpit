namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Implemented by a plugin that has something to give a session when it starts (AC-165) — a variable the tools in
/// that session need, told by the project the session belongs to. The host asks every plugin that registered one,
/// merges the answers, and hands the result to whichever provider is starting, so a contribution arrives the same
/// way whether the session is a Claude CLI, a Codex app-server, a Kimi ACP connection or a TTY.
/// <para>
/// Asked rather than pushed, like <see cref="Mcp.IPluginMcpProvider"/>: the plugin answers with whatever it
/// currently holds, so a value it changes takes effect on the next session without another store to keep in step.
/// What it is told, and that one does not get, is which session and project it is answering for.
/// </para>
/// </summary>
public interface ISessionResourceProvider
{
    /// <summary>
    /// What this plugin gives the session described by <paramref name="request"/>, or
    /// <see cref="SessionResourceContribution.None"/> when it has nothing for this one — the ordinary answer, and
    /// the one to give for a session whose project is not linked to anything of yours.
    /// <para>
    /// Runs while the operator is waiting for the session to open, so keep it short: read what the plugin already
    /// holds rather than reaching for the network. A call that throws is logged and treated as
    /// <see cref="SessionResourceContribution.None"/> — one plugin's bad day does not stop a session starting, and a
    /// session that starts without an endpoint is recoverable in a way one that never starts is not.
    /// </para>
    /// </summary>
    Task<SessionResourceContribution> GetSessionResourcesAsync(SessionResourceRequest request, CancellationToken cancellationToken = default);
}
