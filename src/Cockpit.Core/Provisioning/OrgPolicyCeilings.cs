using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Provisioning;

// AC-1593 (manifest-v1 §3.7): the organisation's ceilings, applied where a session uses them. cockpit.json is never
// rewritten, so a local value above a ceiling stays and counts again once the ceiling goes.
public static class OrgPolicyCeilings
{
    public const string Forbidden = "forbidden";

    public static string? MaxPermissionMode(ProvisionPolicy? policy) => policy?.Permissions?.MaxPermissionMode;

    // The stricter of the two permission ceilings: a delegated task never gets more than a session may start with.
    public static string? DelegationCeiling(ProvisionPolicy? policy) =>
        (policy?.Permissions?.DelegationCeiling, MaxPermissionMode(policy)) switch
        {
            (null, var mode) => mode,
            (var delegation, null) => delegation,
            (var delegation, var mode) => DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(delegation, mode),
        };

    public static bool AutoApproveForbidden(ProvisionPolicy? policy) =>
        string.Equals(policy?.Permissions?.AutoApproveTools, Forbidden, StringComparison.OrdinalIgnoreCase);

    // The mode to use instead of `mode`, or null when it is within `ceiling`. An unknown name is clamped too; a blank
    // one is the driver's own default and is left alone.
    public static string? Clamp(string? ceiling, string? mode) =>
        ceiling is null || string.IsNullOrWhiteSpace(mode)
            || DelegatedToolPermissionPolicy.IsKnownCeiling(mode) && !DelegatedToolPermissionPolicy.IsAboveCeiling(ceiling, mode)
            ? null
            : ceiling;

    public static bool Allows(string? ceiling, string mode) => Clamp(ceiling, mode) is null;

    public static string StartedClamped(ProvisionPolicy policy, string ceiling) =>
        $"Started in {ceiling}: {policy.By} allows at most {ceiling}.";

    public static string SwitchRefused(ProvisionPolicy policy, string mode, string ceiling) =>
        $"Stayed out of {mode}: {policy.By} allows at most {ceiling}.";

    public static string AutoApproveRefused(ProvisionPolicy policy) =>
        $"Tool calls are not approved automatically: {policy.By} does not allow it.";
}
