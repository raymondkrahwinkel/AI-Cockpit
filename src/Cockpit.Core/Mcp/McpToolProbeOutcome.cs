namespace Cockpit.Core.Mcp;

// The Core-level mirror of `Cockpit.Plugins.Abstractions.Mcp.McpProbeOutcome` (AC-503) — kept as its own type
// rather than shared, the same isolation `McpAuthState`/`PluginMcpAuthState` already keep apart:
// `Cockpit.Core` carries no reference to the plugin SDK, so `IMcpToolProbe` answers in this
// vocabulary and the app layer (`CockpitHost`) maps it onto the plugin-facing one, the same seam
// `GetMcpServerAuthStateAsync`/`SignInMcpServerAsync` already use for their own outcomes.
public enum McpToolProbeOutcome
{
    // Failed at 0 for the same reason PluginMcpSignInOutcome.Unavailable is: an unstubbed fake or a call that never
    // ran must never read as "confirmed" or even as the more specific "not found".

    // The call could not be completed, or the tool's own error was too ambiguous to read as a definite "not found".
    Failed,

    // The server needs a sign-in that has not happened; no tool call was attempted.
    NotSignedIn,

    // The tool ran and reported, in a recognisable way, that the value does not resolve.
    NotFound,

    // The tool ran and reported success.
    Success,
}
