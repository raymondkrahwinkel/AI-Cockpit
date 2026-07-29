namespace Cockpit.Core.Mcp;

/// <summary>
/// The Core-level mirror of <c>Cockpit.Plugins.Abstractions.Mcp.McpProbeOutcome</c> (AC-503) — kept as its own type
/// rather than shared, the same isolation <see cref="McpAuthState"/>/<c>PluginMcpAuthState</c> already keep apart:
/// <c>Cockpit.Core</c> carries no reference to the plugin SDK, so <see cref="IMcpToolProbe"/> answers in this
/// vocabulary and the app layer (<c>CockpitHost</c>) maps it onto the plugin-facing one, the same seam
/// <c>GetMcpServerAuthStateAsync</c>/<c>SignInMcpServerAsync</c> already use for their own outcomes.
/// </summary>
public enum McpToolProbeOutcome
{
    // Failed at 0 for the same reason PluginMcpSignInOutcome.Unavailable is: an unstubbed fake or a call that never
    // ran must never read as "confirmed" or even as the more specific "not found".

    /// <summary>The call could not be completed, or the tool's own error was too ambiguous to read as a definite "not found".</summary>
    Failed,

    /// <summary>The server needs a sign-in that has not happened; no tool call was attempted.</summary>
    NotSignedIn,

    /// <summary>The tool ran and reported, in a recognisable way, that the value does not resolve.</summary>
    NotFound,

    /// <summary>The tool ran and reported success.</summary>
    Success,
}
