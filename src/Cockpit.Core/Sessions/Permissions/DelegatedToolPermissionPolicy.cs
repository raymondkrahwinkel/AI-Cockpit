namespace Cockpit.Core.Sessions.Permissions;

// The non-interactive tool-permission decision for a delegated session (AC-79). A delegated session has no
// human to answer a prompt, so a tool call cannot be put to anyone — it is decided here, deterministically,
// against the delegating profile's permission ceiling and its explicit tool allow-list. Pure and static so the
// security decision is exhaustively testable without a running session, and so the same rule is used wherever a
// headless local-model tool call is gated.
//
// Read alongside `ToolPermissionClass`. The ceiling grades what class of tool may run unattended:
// `plan`/`default` allow only read-only, `acceptEdits` also allows a (non-destructive) write, and
// only `bypassPermissions` allows a destructive tool. A tool on the profile's allow-list is the operator's
// explicit "yes" and runs regardless of class — the trust anchor for a tool whose server gives no reliable hint.
// The enabled-server set (the servers the delegation policy exposes at all) is the outer bound and is enforced
// upstream; this only decides among tools that already reached the session.
public static class DelegatedToolPermissionPolicy
{
    // The ceiling a delegated task runs at when its caller asked for nothing more (AC-971): read-only. `default`
    // rather than `plan`, though the two rank the same — plan mode tells a CLI to draft a plan and ask to act on
    // it, and there is nobody to ask.
    public const string ReadOnlyCeiling = "default";

    // The permission ceiling that also allows a non-destructive write, not only read-only tools.
    private const string AcceptEditsCeiling = "acceptEdits";

    // The only ceiling under which a destructive tool runs unattended — the operator's explicit "trust this profile fully".
    private const string BypassPermissionsCeiling = "bypassPermissions";

    // Classifies a tool from its MCP annotations. A read-only tool is `ToolPermissionClass.ReadOnly`;
    // a non-read-only tool is `ToolPermissionClass.Write` only when the server explicitly says it is
    // not destructive, otherwise `ToolPermissionClass.Destructive` (the spec's own default for a
    // non-read-only tool, and the safe reading of an absent hint); no `readOnlyHint` at all is
    // `ToolPermissionClass.Unknown`, since the class genuinely cannot be told.
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

    // A first-party fallback class for a well-known built-in tool whose MCP server ships no reliable
    // read-only/destructive annotation — above all the built-in filesystem preset
    // (`@modelcontextprotocol/server-filesystem`), whose write tools would otherwise be
    // `ToolPermissionClass.Unknown` and denied at every ceiling below `bypassPermissions`, making
    // a local coder profile unable to write a single file at the default `acceptEdits` ceiling (AC-100/AC-112).
    // Returns `null` for a name we do not recognise, so an unrecognised tool keeps its
    // annotation-derived class. This is a table of names only; the caller is responsible for consulting it ONLY
    // for the built-in filesystem preset (identified by its package, `Cockpit.Core.Mcp.McpServerPresets.FilesystemServerPackage`)
    // and ONLY where the server gave no explicit hint — so a rogue server that reuses one of these names never gets
    // the fallback. The filesystem server is itself scoped to one configured folder, so its writes are workspace
    // edits — the exact thing `acceptEdits` is meant to permit — not free rein over the disk.
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

    // A class for the tools an agent CLI runs itself (AC-971). They never reach the host as annotated MCP tools —
    // the CLI owns them and only asks by name — so without this a delegated session's file writes were decided by
    // the CLI's permission mode alone. `Bash` is Destructive because the host cannot see what a command will do.
    public static ToolPermissionClass? ClassifyAgentBuiltIn(string toolName) => toolName switch
    {
        "Read" or "Glob" or "Grep" or "LS" or "NotebookRead" or "WebFetch" or "WebSearch"
            or "TodoWrite" or "Task" or "ExitPlanMode" or "BashOutput"
            => ToolPermissionClass.ReadOnly,

        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "apply_patch"
            => ToolPermissionClass.Write,

        "Bash" or "KillShell" or "KillBash"
            => ToolPermissionClass.Destructive,

        _ => null,
    };

    // Whether `ceiling` lets a delegated session change anything at all (AC-971) — false for `plan`/`default` and for
    // anything unrecognised. Read when judging a finished task's changed-path report: a read-only task that changed
    // files got past a boundary, and that is a failure rather than a result.
    public static bool AllowsChanges(string? ceiling) => _CeilingRank(ceiling) >= 2;

    // Decides whether a delegated session may run `toolName` unattended. An allow-listed tool is
    // always allowed; otherwise the `toolClass` is graded against `ceiling`. An
    // unrecognised ceiling is treated as the most restrictive (read-only only), so a typo or a future mode never
    // silently widens what runs. A denial carries a reason for the tool result the model sees — it is never a
    // hang or a prompt.
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

    // The more restrictive of two classes, for reconciling the same tool name reported by two enabled servers
    // (AC-79). Trust is keyed on the bare tool name, so a name collision across servers is ambiguous: taking the
    // harder-to-run class means a rogue or over-broad server cannot shadow a safe name to widen what runs
    // unattended — the worst case wins. Ordered least- to most-restrained-from-auto-running:
    // ReadOnly &lt; Write &lt; Destructive &lt; Unknown (Unknown never auto-runs without the allow-list).
    public static ToolPermissionClass MoreRestrictive(ToolPermissionClass a, ToolPermissionClass b) =>
        _Restraint(a) >= _Restraint(b) ? a : b;

    // The more restrictive of two permission ceilings, ranked by how much a delegated session may do unattended:
    // `bypassPermissions` &gt; `acceptEdits` &gt; `default`/`plan` &gt; anything unrecognised
    // (treated as most restrictive, so a typo or a future mode never silently widens what runs). Used to find a
    // caller's per-task requested ceiling that falls at or below the profile's own (AC-117): that case needs no
    // second consent and this is always what runs. A request ABOVE the profile's ceiling is a different path
    // (`DelegationService._EffectiveCeilingAsync`, gated by `IsAboveCeiling` rather than a string
    // comparison against this method's result — `default` and `plan` rank equal but are not equal
    // strings, and this method's tie-break would otherwise misreport a same-rank alias as an escalation) — this
    // method never widens on its own; honouring a genuine escalation is the operator's call, through the consent
    // broker.
    public static string MoreRestrictiveCeiling(string? a, string? b) =>
        _CeilingRank(a) <= _CeilingRank(b) ? a ?? string.Empty : b ?? string.Empty;

    // Whether `requested` would let a delegated session do more unattended than
    // `ceiling` allows — the trigger for AC-117's operator-elevation consent. A strict rank
    // comparison, not a string comparison against `MoreRestrictiveCeiling`'s result: `default`
    // and `plan` are distinct strings at the same rank, and treating that tie as an escalation would put a
    // consent prompt in front of a request that asks for no more than the profile already allows.
    public static bool IsAboveCeiling(string? ceiling, string? requested) => _CeilingRank(requested) > _CeilingRank(ceiling);

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
