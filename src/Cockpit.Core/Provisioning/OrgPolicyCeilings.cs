namespace Cockpit.Core.Provisioning;

// AC-1593 (manifest-v1 §3.7): the organisation's ceilings, applied where a session uses them. cockpit.json is never
// rewritten, so a local value above a ceiling stays and counts again once the ceiling goes.
public static class OrgPolicyCeilings
{
    public const string Forbidden = "forbidden";

    public const string Bypass = "bypassPermissions";

    // Option values other providers use for "nothing is asked", the same pairs UnsupervisedProfile reads, with what
    // takes their place below a bypass ceiling.
    private static readonly (string Key, string Value, Func<string, string> Instead)[] _Unsupervised =
    [
        ("sandbox", "danger-full-access", ceiling => _Rank(ceiling) >= _Rank("acceptEdits") ? "workspace-write" : "read-only"),
        ("approvalPolicy", "never", _ => "on-request"),
        ("mode", "yolo", _ => "default"),
        ("mode", "auto", _ => "default"),
    ];

    public static string? MaxPermissionMode(ProvisionPolicy? policy) => policy?.Permissions?.MaxPermissionMode;

    // The stricter of the two permission ceilings: a delegated task never gets more than a session may start with.
    public static string? DelegationCeiling(ProvisionPolicy? policy) => Lower(policy?.Permissions?.DelegationCeiling, MaxPermissionMode(policy));

    // Approving every tool call is what bypass means, so a ceiling below it forbids that as much as the explicit switch.
    public static bool AutoApproveForbidden(ProvisionPolicy? policy) => _ApproveAllForbidden(policy, MaxPermissionMode(policy));

    public static bool DelegatedAutoApproveForbidden(ProvisionPolicy? policy) => _ApproveAllForbidden(policy, DelegationCeiling(policy));

    // The mode to use instead of `mode`, or null when it is within `ceiling`. Its own ordering, with plan below default:
    // an organisation that says plan does not mean "edits after a prompt". An unknown name is clamped too; a blank one
    // is left to the launch clamp, which states a mode instead.
    public static string? Clamp(string? ceiling, string? mode) =>
        ceiling is null || string.IsNullOrWhiteSpace(mode) || _Rank(mode) > 0 && _Rank(mode) <= _Rank(ceiling) ? null : ceiling;

    public static bool Allows(string? ceiling, string mode) => Clamp(ceiling, mode) is null;

    // The stricter of two ceilings, either of which may be absent.
    public static string? Lower(string? a, string? b) => a is null ? b : b is null ? a : _Rank(a) <= _Rank(b) ? a : b;

    // What a provider option must become under `ceiling`, or null when its value is fine there.
    public static string? SafeOption(string? ceiling, string key, string value) =>
        ceiling is null || ceiling == Bypass
            ? null
            : _Unsupervised.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, value.Trim(), StringComparison.OrdinalIgnoreCase)).Instead?.Invoke(ceiling);

    public static bool IsUnsupervisedKey(string key) => _Unsupervised.Any(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string StartedClamped(ProvisionPolicy policy, string ceiling) =>
        $"Started in {ceiling}: {policy.By} allows at most {ceiling}.";

    public static string SwitchRefused(ProvisionPolicy policy, string mode, string ceiling) =>
        $"Stayed out of {mode}: {policy.By} allows at most {ceiling}.";

    public static string AutoApproveRefused(ProvisionPolicy policy) =>
        $"Tool calls are not approved automatically: {policy.By} does not allow it.";

    private static bool _ApproveAllForbidden(ProvisionPolicy? policy, string? ceiling) =>
        string.Equals(policy?.Permissions?.AutoApproveTools, Forbidden, StringComparison.OrdinalIgnoreCase) || ceiling is not null && ceiling != Bypass;

    private static int _Rank(string? mode) => mode switch
    {
        Bypass => 4,
        "acceptEdits" => 3,
        "default" => 2,
        "plan" => 1,
        _ => 0,
    };
}
