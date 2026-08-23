namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin gives a session that is starting (AC-165): the variables its process runs with. Distinct from what
/// a project tells a session (its memory location, the rows it shares), which arrives as standing instructions.
/// </summary>
public sealed record SessionResourceContribution
{
    /// <summary>
    /// A contribution that adds nothing — what a plugin returns when this session is not its business. Cheaper than building an empty one per launch.
    /// </summary>
    public static SessionResourceContribution None { get; } = new();

    /// <summary>
    /// Variables to put in the session's process environment — <c>GH_REPO</c> for the repository this project is
    /// tracked in, say. Applied over the operator's profile variables and under the cockpit's own and the
    /// provider's.
    /// </summary>
    /// <remarks>
    /// A key the host controls — an <c>ANTHROPIC_*</c> credential, a nested-agent marker — is dropped and logged by
    /// name, exactly as a profile's would be. Nothing here is trusted to have been scrubbed already.
    /// </remarks>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether this contribution would change anything, so the host can skip the merge for the ordinary case of a plugin with nothing to add.
    /// </summary>
    public bool IsEmpty => EnvironmentVariables.Count == 0;
}
