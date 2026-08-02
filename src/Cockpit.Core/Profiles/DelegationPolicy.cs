namespace Cockpit.Core.Profiles;

// What a profile allows when another session delegates work to it (#67). A session can hand a task to another
// profile — a cheap local model, a different account — and that spawns a real process under this profile, so
// the profile is where the limits live rather than the calling agent's good intentions.
// The hard fields below are enforced by the cockpit whatever the caller asks for. The soft ones
// (`Purpose`, `Tags`, and the task type a caller declares) are advertised to the
// calling agent so it can choose well — they are a guardrail and an audit trail, not proof of intent: nothing
// can verify that a free-text prompt really only summarises. The real boundaries are the hard ones.
//
// `AllowedAsTarget`:
// Whether this profile may be delegated to at all. Default `false`: delegation spawns a
// process under someone's login, so it is opted into, never inherited.
// `MaxConcurrent`:
// How many delegated tasks may run on this profile at once. Guards the provider's usage pot — several
// sub-sessions on a subscription profile all eat the same limit — and, for a local model, the GPU.
// `AllowedWorkingDirs`:
// The directories a delegated task may run in. Empty means the caller cannot choose one and the profile's own
// default applies, so delegation is never a way to reach an arbitrary part of the filesystem.
// `PermissionCeiling`:
// The most permissive permission mode a delegated task may run under, whatever the caller asks for. A
// delegated session has no human to answer a prompt, so it runs non-interactively — the ceiling is what keeps
// "non-interactive" from quietly meaning "bypass everything".
// `MayDelegateFurther`:
// Whether a task running on this profile may itself delegate. Default `false`: without it, a
// sub-agent handed the orchestrator tools could delegate in a loop.
// `TimeoutMinutes`:
// How long a task may run on this profile before the cockpit stops it. A delegated session has nobody watching
// it, so a model that loops, waits on something that never comes, or simply grinds on would otherwise hold a
// slot — and burn a provider's usage — indefinitely. 0 means no limit.
// `AllowedTaskTypes`: The task categories this profile accepts; empty accepts any.
// `Purpose`: Free text telling a calling agent what this profile is good for.
// `Tags`: Capability tags (`code`, `summarize`, `cheap`, `local`, …) for selection.
// `AllowedTools`:
// Tool names a delegated session on this profile may run unattended even when its class or the ceiling would
// otherwise block it (AC-79). A delegated local-model session has no human to answer a permission prompt, and an
// MCP tool's read-only/destructive annotation is only an advisory, server-supplied hint — so a tool the operator
// explicitly listed here is the trust anchor: it runs, and an unclassifiable tool that is *not* listed
// does not. Empty/null means only the ceiling grades tool calls. Ignored when the profile runs tools with
// "Auto-Approve tool calls" on, which already allows everything.
public sealed record DelegationPolicy(
    bool AllowedAsTarget = false,
    int MaxConcurrent = 1,
    IReadOnlyList<string>? AllowedWorkingDirs = null,
    string PermissionCeiling = DelegationPolicy.DefaultPermissionCeiling,
    bool MayDelegateFurther = false,
    int TimeoutMinutes = DelegationPolicy.DefaultTimeoutMinutes,
    IReadOnlyList<string>? AllowedTaskTypes = null,
    string? Purpose = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? AllowedTools = null)
{
    // Delegated tasks run under this mode unless the profile allows a more permissive one.
    public const string DefaultPermissionCeiling = "acceptEdits";

    // Long enough for real work, short enough that a stuck task does not hold a slot all afternoon.
    public const int DefaultTimeoutMinutes = 15;

    // A profile with no policy of its own: not a delegation target.
    public static DelegationPolicy None { get; } = new();
}
