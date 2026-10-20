using Cockpit.Core.Provisioning;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1593: how a choice the organisation's ceiling rules out looks — still listed, not selectable, and the field says
// "Managed by …" in the words AC-1507 uses for a locked setting.
public static class OrgPolicyMarking
{
    public static IReadOnlyList<SelectableChoice> Choices(string key, IReadOnlyList<SelectableChoice> items, ProvisionPolicy? policy) =>
        _Ceiling(key, policy) is { } ceiling
            ? [.. items.Select(item => item with { IsEnabled = OrgPolicyCeilings.Allows(ceiling, item.Value) })]
            : items;

    public static string? Label(string key, ProvisionPolicy? policy) => _Ceiling(key, policy) is null ? null : ManagedBy(policy);

    // The ceiling itself when `value` is above it, so a launch choice opens on what will actually run.
    public static string? Snapped(string key, string? value, ProvisionPolicy? policy) =>
        _Ceiling(key, policy) is { } ceiling ? OrgPolicyCeilings.Clamp(ceiling, value) : null;

    public static string? AutoApproveLabel(ProvisionPolicy? policy) => OrgPolicyCeilings.AutoApproveForbidden(policy) ? ManagedBy(policy) : null;

    public static string? ManagedBy(ProvisionPolicy? policy) => policy is null ? null : $"Managed by {policy.By}";

    private static string? _Ceiling(string key, ProvisionPolicy? policy) =>
        string.Equals(key, WellKnownPluginSessionOptions.PermissionMode, StringComparison.OrdinalIgnoreCase) ? OrgPolicyCeilings.MaxPermissionMode(policy) : null;
}
