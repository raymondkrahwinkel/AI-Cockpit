namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// What came of a single, out-of-session tool call a plugin asked the host to make on its behalf
/// (<see cref="ICockpitHost.ProbeMcpToolAsync"/>, AC-503) — used to confirm that a value the operator typed
/// (a Depot project slug, say) actually resolves to something, without a running session to ask through.
/// </summary>
public enum McpProbeOutcome
{
    // Failed is deliberately the zero value — the same reasoning as PluginMcpSignInOutcome.Unavailable: an
    // unstubbed Substitute.For<ICockpitHost>() (or any other unconfigured Task<T> fake) must never read as
    // "confirmed", nor even as the more specific "not found". A default that lands here says only "nothing was
    // confirmed", which is the one claim that is always true of a call that never actually ran.

    /// <summary>
    /// The call could not be completed and nothing about the value itself was learned — a timeout, a network
    /// failure, an unexpected exception, or a tool error too ambiguous to read as a definite "not found". Never
    /// shown as "does not exist": the honest reading is "could not confirm", not a claim about the value.
    /// </summary>
    Failed,

    /// <summary>
    /// The server needs a sign-in that has not happened (or whose token can no longer be renewed silently) —
    /// no tool call was attempted, the same restraint <see cref="ICockpitHost.GetMcpServerAuthStateAsync"/> already
    /// takes before a session ever tries this server.
    /// </summary>
    NotSignedIn,

    /// <summary>The tool ran and reported, in a way this probe can actually recognise, that the value does not resolve to anything.</summary>
    NotFound,

    /// <summary>The tool ran and reported success. <see cref="McpProbeResult.Detail"/> carries its raw text output.</summary>
    Success,
}
