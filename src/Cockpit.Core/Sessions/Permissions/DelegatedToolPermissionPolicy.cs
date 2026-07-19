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
    /// A first-party fallback class for a well-known built-in tool whose MCP server ships no reliable
    /// read-only/destructive annotation — above all the built-in filesystem preset
    /// (<c>@modelcontextprotocol/server-filesystem</c>), whose write tools would otherwise be
    /// <see cref="ToolPermissionClass.Unknown"/> and denied at every ceiling below <c>bypassPermissions</c>, making
    /// a local coder profile unable to write a single file at the default <c>acceptEdits</c> ceiling (AC-100/AC-112).
    /// Returns <see langword="null"/> for a name we do not recognise, so an unrecognised tool keeps its
    /// annotation-derived class. Keyed on the bare tool name to match how the delegated gate keys trust; the
    /// more-restrictive reconciliation in the tool provider still applies if another server exposes the same name.
    /// Only ever consulted where the server did not declare the tool explicitly, so an explicit hint is never widened.
    /// The filesystem server is itself scoped to one configured folder, so its writes are workspace edits — the exact
    /// thing <c>acceptEdits</c> is meant to permit — not free rein over the disk.
    /// </summary>
    public static ToolPermissionClass? ClassifyWellKnown(string toolName) => toolName switch
    {
        // @modelcontextprotocol/server-filesystem — read side.
        "read_file" or "read_text_file" or "read_media_file" or "read_multiple_files"
            or "list_directory" or "list_directory_with_sizes" or "directory_tree"
            or "search_files" or "get_file_info" or "list_allowed_directories"
            => ToolPermissionClass.ReadOnly,

        // @modelcontextprotocol/server-filesystem — write side. State-changing but not destructive: the server is
        // scoped to a single configured folder, so these edit files within the workspace rather than delete freely.
        "write_file" or "edit_file" or "create_directory" or "move_file"
            => ToolPermissionClass.Write,

        _ => null,
    };

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

    /// <summary>
    /// The more restrictive of two permission ceilings, ranked by how much a delegated session may do unattended:
    /// <c>bypassPermissions</c> &gt; <c>acceptEdits</c> &gt; <c>default</c>/<c>plan</c> &gt; anything unrecognised
    /// (treated as most restrictive, so a typo or a future mode never silently widens what runs). Used to clamp a
    /// caller's per-task requested ceiling to the profile's own (AC-117): a request can only ever narrow what the
    /// operator already allowed, never widen it, so it is always safe to honour without a second consent.
    /// </summary>
    public static string MoreRestrictiveCeiling(string? a, string? b) =>
        _CeilingRank(a) <= _CeilingRank(b) ? a ?? string.Empty : b ?? string.Empty;

    private static int _CeilingRank(string? ceiling) => ceiling switch
    {
        BypassPermissionsCeiling => 3,
        AcceptEditsCeiling => 2,
        "default" or "plan" => 1,
        _ => 0, // unrecognised/blank — most restrictive (read-only only), the fail-safe reading
    };

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
