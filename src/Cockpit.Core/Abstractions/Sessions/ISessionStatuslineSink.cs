namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Sets a session's statusline from outside the UI layer (#AC-13) — the seam the first-party orchestrator MCP
/// server uses to let an agent set what its own session is working on, without the Infrastructure layer
/// referencing the App view-models. Core declares it, Infrastructure calls it, the App implements it over its
/// session view-models and marshals to the UI thread — the same direction as
/// <see cref="Delegation.IDelegationService.TasksChanged"/>. Kept off the plugin-facing <c>ICockpitHost</c> surface
/// because this is a host-internal service, not a plugin capability.
/// </summary>
public interface ISessionStatuslineSink
{
    /// <summary>
    /// Sets the statusline of the session identified by <paramref name="paneId"/> (its pane id). Returns whether a
    /// live session matched — <see langword="false"/> is a no-op (the session may have closed), never an error. An
    /// empty <paramref name="statusline"/> clears it.
    /// </summary>
    Task<bool> SetStatuslineAsync(string paneId, string statusline);
}

/// <summary>
/// No-op <see cref="ISessionStatuslineSink"/> for any context without the App view-model layer (a headless or
/// test host): reports nothing matched rather than failing. The App registers a live sink over its cockpit.
/// </summary>
public sealed class NullSessionStatuslineSink : ISessionStatuslineSink
{
    public static NullSessionStatuslineSink Instance { get; } = new();

    private NullSessionStatuslineSink()
    {
    }

    public Task<bool> SetStatuslineAsync(string paneId, string statusline) => Task.FromResult(false);
}
