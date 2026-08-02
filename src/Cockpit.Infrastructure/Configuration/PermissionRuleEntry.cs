using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a single `PermissionRule` in the `permissionRules` section.
// Stores the scope as its enum name so the JSON stays human-readable (`Exact`/`Wildcard`).
internal sealed class PermissionRuleEntry
{
    public string ToolName { get; set; } = string.Empty;

    public PermissionRuleScope Scope { get; set; }

    public string? InputMatch { get; set; }

    public static PermissionRuleEntry FromDomain(PermissionRule rule) => new()
    {
        ToolName = rule.ToolName,
        Scope = rule.Scope,
        InputMatch = rule.InputMatch,
    };

    public PermissionRule ToDomain() => new(ToolName, Scope, InputMatch);
}
