using Cockpit.Core.Claude.Permissions;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a single <see cref="PermissionRule"/> in the <c>permissionRules</c> section.
/// Stores the scope as its enum name so the JSON stays human-readable (<c>Exact</c>/<c>Wildcard</c>).
/// </summary>
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
