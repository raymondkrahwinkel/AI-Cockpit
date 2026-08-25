namespace Cockpit.Core.Profiles;

// AC-1013: Delegation limits (#67) enforced by the cockpit regardless of what a caller asks for — hard fields
// (AllowedAsTarget/MaxConcurrent/AllowedWorkingDirs/PermissionCeiling/MayDelegateFurther/TimeoutMinutes/AllowedTools)
// are enforced; soft fields (Purpose/Tags/AllowedTaskTypes) are only advisory to the calling agent, not proof of intent.
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
