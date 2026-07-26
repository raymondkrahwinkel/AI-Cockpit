namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Sets what a session is called and what it says it is doing, from outside the UI layer (#AC-13, #AC-312) — the seam
/// the first-party <c>cockpit-session</c> MCP server uses to let an agent label its own session, without the
/// Infrastructure layer referencing the App view-models. Core declares it, Infrastructure calls it, the App implements
/// it over its session view-models and marshals to the UI thread — the same direction as
/// <see cref="Delegation.IDelegationService.TasksChanged"/>. Kept off the plugin-facing <c>ICockpitHost</c> surface
/// because this is a host-internal service, not a plugin capability.
/// </summary>
public interface ISessionLabelSink
{
    /// <summary>
    /// Sets the statusline of the session identified by <paramref name="paneId"/> (its pane id). Returns whether a
    /// live session matched — <see langword="false"/> is a no-op (the session may have closed), never an error. An
    /// empty <paramref name="statusline"/> clears it.
    /// </summary>
    Task<bool> SetStatuslineAsync(string paneId, string statusline);

    /// <summary>
    /// Proposes <paramref name="name"/> as the title of the session identified by <paramref name="paneId"/>, and
    /// stands down when that session already carries a name somebody chose (#AC-310). Returns whether it was
    /// renamed, so <see langword="false"/> covers "no such session", "blank name" and "it has a name of its own"
    /// alike — none of the three is an error. Proposing rather than setting is the whole point on this seam: an
    /// agent labelling itself after the ticket it picked up is useful, an agent overwriting the name its operator
    /// typed is not.
    /// </summary>
    Task<bool> SuggestNameAsync(string paneId, string name);
}

/// <summary>
/// No-op <see cref="ISessionLabelSink"/> for any context without the App view-model layer (a headless or
/// test host): reports nothing matched rather than failing. The App registers a live sink over its cockpit.
/// </summary>
public sealed class NullSessionLabelSink : ISessionLabelSink
{
    public static NullSessionLabelSink Instance { get; } = new();

    private NullSessionLabelSink()
    {
    }

    public Task<bool> SetStatuslineAsync(string paneId, string statusline) => Task.FromResult(false);

    public Task<bool> SuggestNameAsync(string paneId, string name) => Task.FromResult(false);
}
