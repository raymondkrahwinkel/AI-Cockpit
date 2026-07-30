namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What a Memory row's own check (AC-503) found about the value the operator typed against a plugin-registered
/// <see cref="ProjectMemorySourceRegistration"/> — the plugin-resource half of the confirmation the project editor
/// already shows a <c>Reference</c> row that names a broken absolute path (AC-485,
/// <c>Cockpit.Infrastructure.Projects.ProjectResourceProbe</c>).
/// <para>
/// <see cref="NotFound"/>, not four states, still stands for "exists but this operator/token cannot see it" as well
/// as "does not exist" — DEP-136 (not yet built) is what would let those two be told apart. What changed (AC-499): a
/// plugin whose own vocabulary <em>does</em> distinguish "needs a sign-in" from "ran and errored for another
/// reason" — <c>ICockpitHost.CallMcpToolAsync</c>'s <c>PluginMcpToolCallOutcome</c> does, unlike the old
/// <c>ProbeMcpToolAsync</c>-based <c>McpProbeOutcome</c> this type's <see cref="NotSignedIn"/> was originally built
/// against — now has <see cref="CheckFailed"/> to report that in rather than being forced to conflate it with "needs
/// a sign-in", which is exactly the defect AC-499 traced: an operator who was already signed in, reading "sign in to
/// confirm this location" for a check that had in fact run and failed for an unrelated reason. A plugin that still
/// cannot tell the two apart may keep answering <see cref="NotSignedIn"/> for both — that stays a safe, honest
/// fallback, just no longer the only option.
/// </para>
/// </summary>
public enum ProjectMemorySourceReachability
{
    // NotSignedIn at 0 for the same reason PluginMcpSignInOutcome.Unavailable and McpProbeOutcome.Failed are: an
    // unstubbed test fake, or a plugin's own check delegate throwing before it decides anything, must never read as
    // a specific claim about the value — neither "confirmed" (the best case) nor "not found" (a definite, and
    // potentially wrong, negative). Landing on the vaguest state — "cannot tell right now, might need action" — is
    // the one thing that is always honestly true of an unanswered check.

    /// <summary>
    /// The source needs a sign-in (or reauthorization) that has not happened — the one state that actually means
    /// "go sign in". A plugin unable to tell this apart from an ordinary failed call (see <see cref="CheckFailed"/>)
    /// may still report this as a safe fallback for both, the same restraint this type held before AC-499 added a
    /// state for the case a plugin <em>can</em> tell apart.
    /// </summary>
    NotSignedIn,

    /// <summary>The check ran, reached the connection, and the value does not resolve to anything — or resolves to something this operator/token cannot see (see the type's own remarks on DEP-136).</summary>
    NotFound,

    /// <summary>The check ran and confirmed the value. <see cref="ProjectMemorySourceReachabilityResult.Detail"/> carries what it found, if anything.</summary>
    Confirmed,

    /// <summary>
    /// The connection is signed in, but the check itself could not complete — a network hiccup, a tool error, an
    /// unparsable response (AC-499). Distinct from <see cref="NotSignedIn"/> precisely so "you need to sign in
    /// again" is never shown for a call that already proved the sign-in works. Never a claim about the value itself
    /// (never <see cref="NotFound"/>-like certainty) — only that nothing could be confirmed either way this time.
    /// <see cref="ProjectMemorySourceReachabilityResult.Detail"/> carries what went wrong, if the plugin said.
    /// </summary>
    CheckFailed,
}
