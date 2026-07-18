namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// The non-interactive tool-permission decision for a delegated session (AC-79). A delegated session has no
/// human to answer a prompt, so a tool call cannot be put to anyone — it is decided here, deterministically,
/// against the delegating profile's permission ceiling and its explicit tool allow-list. Pure and static so the
/// security decision is exhaustively testable without a running session, and so the same rule is used wherever a
/// headless local-model tool call is gated.
/// <para>
/// Read alongside <see cref="ToolPermissionClass"/>. The ceiling grades what class of tool may run unattended:
/// <c>plan</c>/<c>default</c> allow only read-only, <c>acceptEdits</c> also allows a (non-destructive) write, and
/// only <c>bypassPermissions</c> allows a destructive tool. A tool on the profile's allow-list is the operator's
/// explicit "yes" and runs regardless of class — the trust anchor for a tool whose server gives no reliable hint.
/// The enabled-server set (the servers the delegation policy exposes at all) is the outer bound and is enforced
/// upstream; this only decides among tools that already reached the session.
/// </para>
/// </summary>
public static class DelegatedToolPermissionPolicy
{
    /// <summary>The permission ceiling that also allows a non-destructive write, not only read-only tools.</summary>
    private const string AcceptEditsCeiling = "acceptEdits";

    /// <summary>The only ceiling under which a destructive tool runs unattended — the operator's explicit "trust this profile fully".</summary>
    private const string BypassPermissionsCeiling = "bypassPermissions";

    /// <summary>
    /// Classifies a tool from its MCP annotations. A read-only tool is <see cref="ToolPermissionClass.ReadOnly"/>;
    /// a non-read-only tool is <see cref="ToolPermissionClass.Write"/> only when the server explicitly says it is
    /// not destructive, otherwise <see cref="ToolPermissionClass.Destructive"/> (the spec's own default for a
    /// non-read-only tool, and the safe reading of an absent hint); no <paramref name="readOnlyHint"/> at all is
    /// <see cref="ToolPermissionClass.Unknown"/>, since the class genuinely cannot be told.
    /// </summary>
    public static ToolPermissionClass Classify(bool? readOnlyHint, bool? destructiveHint)
    {
        if (readOnlyHint == true)
        {
            return ToolPermissionClass.ReadOnly;
        }

        if (readOnlyHint == false)
        {
            return destructiveHint == false ? ToolPermissionClass.Write : ToolPermissionClass.Destructive;
        }

        return ToolPermissionClass.Unknown;
    }

    /// <summary>
    /// Decides whether a delegated session may run <paramref name="toolName"/> unattended. An allow-listed tool is
    /// always allowed; otherwise the <paramref name="toolClass"/> is graded against <paramref name="ceiling"/>. An
    /// unrecognised ceiling is treated as the most restrictive (read-only only), so a typo or a future mode never
    /// silently widens what runs. A denial carries a reason for the tool result the model sees — it is never a
    /// hang or a prompt.
    /// </summary>
    public static PermissionDecision Decide(string? ceiling, ToolPermissionClass toolClass, string toolName, bool onAllowList)
    {
        if (onAllowList)
        {
            return PermissionDecision.Allow();
        }

        var normalizedCeiling = ceiling ?? string.Empty;

        return toolClass switch
        {
            ToolPermissionClass.ReadOnly => PermissionDecision.Allow(),

            ToolPermissionClass.Write when string.Equals(normalizedCeiling, AcceptEditsCeiling, StringComparison.Ordinal)
                                        || string.Equals(normalizedCeiling, BypassPermissionsCeiling, StringComparison.Ordinal)
                => PermissionDecision.Allow(),

            ToolPermissionClass.Destructive when string.Equals(normalizedCeiling, BypassPermissionsCeiling, StringComparison.Ordinal)
                => PermissionDecision.Allow(),

            ToolPermissionClass.Unknown => PermissionDecision.Deny(
                $"Tool '{toolName}' was blocked: its MCP server gives no read-only/destructive hint, so a delegated session cannot classify it, and it is not on the delegating profile's tool allow-list. Add it to the profile's auto-runnable tools, or run this profile with 'Auto-Approve tool calls' on, to allow it."),

            _ => PermissionDecision.Deny(
                $"Tool '{toolName}' ({_Describe(toolClass)}) was blocked: the delegating profile's permission ceiling '{(string.IsNullOrEmpty(normalizedCeiling) ? "(none)" : normalizedCeiling)}' does not permit it to run unattended, and it is not on the profile's tool allow-list."),
        };
    }

    /// <summary>
    /// The more restrictive of two classes, for reconciling the same tool name reported by two enabled servers
    /// (AC-79). Trust is keyed on the bare tool name, so a name collision across servers is ambiguous: taking the
    /// harder-to-run class means a rogue or over-broad server cannot shadow a safe name to widen what runs
    /// unattended — the worst case wins. Ordered least- to most-restrained-from-auto-running:
    /// ReadOnly &lt; Write &lt; Destructive &lt; Unknown (Unknown never auto-runs without the allow-list).
    /// </summary>
    public static ToolPermissionClass MoreRestrictive(ToolPermissionClass a, ToolPermissionClass b) =>
        _Restraint(a) >= _Restraint(b) ? a : b;

    private static int _Restraint(ToolPermissionClass toolClass) => toolClass switch
    {
        ToolPermissionClass.ReadOnly => 0,
        ToolPermissionClass.Write => 1,
        ToolPermissionClass.Destructive => 2,
        _ => 3, // Unknown — denied unless allow-listed, the most restrained
    };

    private static string _Describe(ToolPermissionClass toolClass) => toolClass switch
    {
        ToolPermissionClass.Write => "a state-changing tool",
        ToolPermissionClass.Destructive => "a destructive tool",
        _ => "a restricted tool",
    };
}
