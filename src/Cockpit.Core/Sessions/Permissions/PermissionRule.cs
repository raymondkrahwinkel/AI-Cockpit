namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// How broadly an always-allow rule matches a proposed tool call.
/// </summary>
public enum PermissionRuleScope
{
    /// <summary>Matches only the same tool with the same input (see <see cref="PermissionRule.InputMatch"/>).</summary>
    Exact,

    /// <summary>Matches any call to the same tool, regardless of input.</summary>
    Wildcard,
}

/// <summary>
/// A persisted "always allow" rule for one profile: the operator chose to stop being prompted for
/// a given tool call. A <see cref="PermissionRuleScope.Wildcard"/> rule allows every call to
/// <see cref="ToolName"/>; an <see cref="PermissionRuleScope.Exact"/> rule allows only the same
/// input, identified by the canonical <see cref="InputMatch"/> fingerprint of the input JSON.
/// </summary>
/// <param name="ToolName">The tool this rule applies to (e.g. <c>Bash</c>, <c>Edit</c>).</param>
/// <param name="Scope">Whether the rule matches any input (wildcard) or one specific input (exact).</param>
/// <param name="InputMatch">
/// The canonical fingerprint of the allowed input for an <see cref="PermissionRuleScope.Exact"/>
/// rule (see <see cref="PermissionInputMatch.Canonicalize"/>); <see langword="null"/> for a wildcard rule.
/// </param>
public sealed record PermissionRule(string ToolName, PermissionRuleScope Scope, string? InputMatch = null)
{
    /// <summary>
    /// True when this rule allows a proposed call to <paramref name="toolName"/> with
    /// <paramref name="proposedInputJson"/>. Wildcard matches on tool name alone; exact also
    /// requires the input to canonicalize to the same fingerprint this rule was stored with.
    /// </summary>
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

    /// <summary>Builds the rule the operator's "always allow" choice should persist for this call.</summary>
    public static PermissionRule ForExact(string toolName, string proposedInputJson) =>
        new(toolName, PermissionRuleScope.Exact, PermissionInputMatch.Canonicalize(proposedInputJson));

    /// <summary>Builds a wildcard rule allowing every future call to <paramref name="toolName"/>.</summary>
    public static PermissionRule ForWildcard(string toolName) =>
        new(toolName, PermissionRuleScope.Wildcard);
}
