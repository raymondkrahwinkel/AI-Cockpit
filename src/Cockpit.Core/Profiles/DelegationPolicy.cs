namespace Cockpit.Core.Profiles;

/// <summary>
/// What a profile allows when another session delegates work to it (#67). A session can hand a task to another
/// profile — a cheap local model, a different account — and that spawns a real process under this profile, so
/// the profile is where the limits live rather than the calling agent's good intentions.
/// </summary>
/// <remarks>
/// The hard fields below are enforced by the cockpit whatever the caller asks for. The soft ones
/// (<see cref="Purpose"/>, <see cref="Tags"/>, and the task type a caller declares) are advertised to the
/// calling agent so it can choose well — they are a guardrail and an audit trail, not proof of intent: nothing
/// can verify that a free-text prompt really only summarises. The real boundaries are the hard ones.
/// </remarks>
/// <param name="AllowedAsTarget">
/// Whether this profile may be delegated to at all. Default <see langword="false"/>: delegation spawns a
/// process under someone's login, so it is opted into, never inherited.
/// </param>
/// <param name="MaxConcurrent">
/// How many delegated tasks may run on this profile at once. Guards the provider's usage pot — several
/// sub-sessions on a subscription profile all eat the same limit — and, for a local model, the GPU.
/// </param>
/// <param name="AllowedWorkingDirs">
/// The directories a delegated task may run in. Empty means the caller cannot choose one and the profile's own
/// default applies, so delegation is never a way to reach an arbitrary part of the filesystem.
/// </param>
/// <param name="PermissionCeiling">
/// The most permissive permission mode a delegated task may run under, whatever the caller asks for. A
/// delegated session has no human to answer a prompt, so it runs non-interactively — the ceiling is what keeps
/// "non-interactive" from quietly meaning "bypass everything".
/// </param>
/// <param name="MayDelegateFurther">
/// Whether a task running on this profile may itself delegate. Default <see langword="false"/>: without it, a
/// sub-agent handed the orchestrator tools could delegate in a loop.
/// </param>
/// <param name="AllowedTaskTypes">The task categories this profile accepts; empty accepts any.</param>
/// <param name="Purpose">Free text telling a calling agent what this profile is good for.</param>
/// <param name="Tags">Capability tags (<c>code</c>, <c>summarize</c>, <c>cheap</c>, <c>local</c>, …) for selection.</param>
public sealed record DelegationPolicy(
    bool AllowedAsTarget = false,
    int MaxConcurrent = 1,
    IReadOnlyList<string>? AllowedWorkingDirs = null,
    string PermissionCeiling = DelegationPolicy.DefaultPermissionCeiling,
    bool MayDelegateFurther = false,
    IReadOnlyList<string>? AllowedTaskTypes = null,
    string? Purpose = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>Delegated tasks run under this mode unless the profile allows a more permissive one.</summary>
    public const string DefaultPermissionCeiling = "acceptEdits";

    /// <summary>A profile with no policy of its own: not a delegation target.</summary>
    public static DelegationPolicy None { get; } = new();
}
