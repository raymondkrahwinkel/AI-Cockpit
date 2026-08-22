namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Sets what a session is called and what it says it is doing, from outside the UI layer (#AC-13, #AC-312) —
/// the seam the <c>cockpit-session</c> MCP server uses to let an agent label its own session without
/// Infrastructure referencing App view-models. Off <c>ICockpitHost</c>: host-internal, not a plugin capability.
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
    /// stands down when it already carries a name somebody chose (#AC-310). Returns whether it was renamed — <see
    /// langword="false"/> covers "no such session", "blank name" and "already named" alike, none an error.
    /// </summary>
    Task<bool> SuggestNameAsync(string paneId, string name);
}

// No-op `ISessionLabelSink` for any context without the App view-model layer (a headless or
// test host): reports nothing matched rather than failing. The App registers a live sink over its cockpit.
public sealed class NullSessionLabelSink : ISessionLabelSink
{
    public static NullSessionLabelSink Instance { get; } = new();

    private NullSessionLabelSink()
    {
    }

    public Task<bool> SetStatuslineAsync(string paneId, string statusline) => Task.FromResult(false);

    public Task<bool> SuggestNameAsync(string paneId, string name) => Task.FromResult(false);
}
