namespace Cockpit.Core.Sessions.Permissions;

// How broadly an always-allow rule matches a proposed tool call.
public enum PermissionRuleScope
{
    // Matches only the same tool with the same input (see `PermissionRule.InputMatch`).
    Exact,

    // Matches any call to the same tool, regardless of input.
    Wildcard,
}

// A persisted "always allow" rule for one profile: the operator chose to stop being prompted for a given
// tool call. `Wildcard` allows every call to `ToolName`; `Exact` allows only the same input, identified by
// the canonical `InputMatch` fingerprint (`PermissionInputMatch.Canonicalize`), null for a wildcard rule.
public sealed record PermissionRule(string ToolName, PermissionRuleScope Scope, string? InputMatch = null)
{
    // True when this rule allows a proposed call to `toolName` with
    // `proposedInputJson`. Wildcard matches on tool name alone; exact also
    // requires the input to canonicalize to the same fingerprint this rule was stored with.
    public bool Matches(string toolName, string proposedInputJson)
    {
        if (!string.Equals(ToolName, toolName, StringComparison.Ordinal))
        {
            return false;
        }

        return Scope switch
        {
            PermissionRuleScope.Wildcard => true,
            PermissionRuleScope.Exact => string.Equals(InputMatch, PermissionInputMatch.Canonicalize(proposedInputJson), StringComparison.Ordinal),
            _ => false,
        };
    }

    // Builds the rule the operator's "always allow" choice should persist for this call.
    public static PermissionRule ForExact(string toolName, string proposedInputJson) =>
        new(toolName, PermissionRuleScope.Exact, PermissionInputMatch.Canonicalize(proposedInputJson));

    // Builds a wildcard rule allowing every future call to `toolName`.
    public static PermissionRule ForWildcard(string toolName) =>
        new(toolName, PermissionRuleScope.Wildcard);
}
