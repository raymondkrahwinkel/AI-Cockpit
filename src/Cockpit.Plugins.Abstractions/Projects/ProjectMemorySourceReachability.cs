namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What a Memory row's own check (AC-503) found about the value the operator typed against a plugin-registered
/// <see cref="ProjectMemorySourceRegistration"/> — the plugin-resource half of the confirmation the project editor
/// already shows a <c>Reference</c> row that names a broken absolute path (AC-485,
/// <c>Cockpit.Infrastructure.Projects.ProjectResourceProbe</c>).
/// <para>
/// Exactly three states, not four: DEP-136 (not yet built) is what would let "does not exist" and "exists but this
/// operator/token cannot see it" be told apart. Until it is, both read as <see cref="NotFound"/> — one eerlijke,
/// niet-misleidende melding for both, recorded where the mapping happens rather than guessed at silently.
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
    /// No usable answer could be obtained — because the connection this check depends on is not signed in, or
    /// because reaching it failed (network, timeout, an unexpected exception). Both read the same way here on
    /// purpose (AC-503 acceptance criterion 4): a transient failure must never be shown as "this does not exist",
    /// which would name the wrong cause.
    /// </summary>
    NotSignedIn,

    /// <summary>The check ran, reached the connection, and the value does not resolve to anything — or resolves to something this operator/token cannot see (see the type's own remarks on DEP-136).</summary>
    NotFound,

    /// <summary>The check ran and confirmed the value. <see cref="ProjectMemorySourceReachabilityResult.Detail"/> carries what it found, if anything.</summary>
    Confirmed,
}
