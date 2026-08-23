namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What a Memory row's own check (AC-503) found about the value the operator typed against a plugin-registered
/// <see cref="ProjectMemorySourceRegistration"/>.
/// </summary>
/// <remarks>
/// <see cref="NotFound"/> stands for both "does not exist" and "exists but this operator/token cannot see it".
/// <see cref="CheckFailed"/> (AC-499) is for a plugin whose own vocabulary distinguishes "needs a sign-in" from
/// "ran and errored for another reason"; a plugin that cannot tell them apart may keep answering
/// <see cref="NotSignedIn"/> for both.
/// </remarks>
public enum ProjectMemorySourceReachability
{
    // NotSignedIn at 0 for the same reason PluginMcpSignInOutcome.Unavailable and McpProbeOutcome.Failed are: an
    // unstubbed fake, or a check delegate throwing before it decides anything, must never read as a specific claim
    // ("confirmed" or "not found"). The vaguest state is the one thing always honestly true of an unanswered check.

    /// <summary>
    /// The source needs a sign-in (or reauthorization) that has not happened.
    /// </summary>
    /// <remarks>
    /// A plugin unable to tell this apart from an ordinary failed call (see <see cref="CheckFailed"/>) may still
    /// report this as a safe fallback for both.
    /// </remarks>
    NotSignedIn,

    /// <summary>
    /// The check ran, reached the connection, and the value does not resolve to anything — or resolves to something this operator/token cannot see (see the type's own remarks on DEP-136).
    /// </summary>
    NotFound,

    /// <summary>
    /// The check ran and confirmed the value. <see cref="ProjectMemorySourceReachabilityResult.Detail"/> carries what it found, if anything.
    /// </summary>
    Confirmed,

    /// <summary>
    /// The connection is signed in, but the check itself could not complete — a network hiccup, a tool error, an
    /// unparsable response (AC-499).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NotSignedIn"/> so "you need to sign in again" is never shown for a call that
    /// already proved the sign-in works. Never a claim about the value itself.
    /// </remarks>
    CheckFailed,
}
