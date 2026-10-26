using Cockpit.Core.Provisioning;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1593: how a choice the organisation's ceiling rules out looks — still listed, not selectable, and the field says
// "Managed by …" in the words AC-1507 uses for a locked setting.
public static class OrgPolicyMarking
{
    public static IReadOnlyList<SelectableChoice> Choices(string key, IReadOnlyList<SelectableChoice> items, ProvisionPolicy? policy) =>
        _Governs(key, policy) is { } ceiling
            ? [.. items.Select(item => item with { IsEnabled = _Replacement(key, item.Value, ceiling) is null })]
            : items;

    public static string? Label(string key, ProvisionPolicy? policy) => AtMost(policy, _Governs(key, policy));

    public static string? AtMost(ProvisionPolicy? policy, string? ceiling) => ceiling is null ? null : $"{ManagedBy(policy)}: at most {ceiling}";

    // What a launch choice should open on, so it shows what will actually run; null when `value` is fine.
    public static string? Snapped(string key, string? value, ProvisionPolicy? policy) =>
        _Governs(key, policy) is { } ceiling && value is not null ? _Replacement(key, value, ceiling) : null;

    public static string? AutoApproveLabel(ProvisionPolicy? policy) =>
        OrgPolicyCeilings.AutoApproveForbidden(policy) ? $"{ManagedBy(policy)}: off in every session" : null;

    public static string? ManagedBy(ProvisionPolicy? policy) => policy is null ? null : $"Managed by {policy.By}";

    // The ceiling when this key is one the mode ceiling reaches: the mode itself, or a provider's "ask nothing" option.
    private static string? _Governs(string key, ProvisionPolicy? policy) =>
        OrgPolicyCeilings.MaxPermissionMode(policy) is { } ceiling && (_IsMode(key) || OrgPolicyCeilings.IsUnsupervisedKey(key) && ceiling != OrgPolicyCeilings.Bypass)
            ? ceiling
            : null;

    private static string? _Replacement(string key, string value, string ceiling) =>
        _IsMode(key) ? OrgPolicyCeilings.Clamp(ceiling, value) : OrgPolicyCeilings.SafeOption(ceiling, key, value);

    private static bool _IsMode(string key) => string.Equals(key, WellKnownPluginSessionOptions.PermissionMode, StringComparison.OrdinalIgnoreCase);
}
