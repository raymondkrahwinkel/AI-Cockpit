namespace Cockpit.Core.Sessions.Permissions;

// AC-79: the non-interactive tool-permission decision for a delegated session — there is no human to
// answer a prompt, so this decides deterministically against the profile's ceiling and allow-list. Pure and
// static so the security decision is exhaustively testable. Read alongside `ToolPermissionClass`.
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

    // Classifies a tool from its MCP annotations: read-only if hinted so; else Write only when the server
    // explicitly says non-destructive, otherwise Destructive (the spec's own default/safe reading); no hint
    // at all is Unknown, since the class genuinely cannot be told.
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

    // AC-100/AC-112: fallback class for the built-in filesystem preset, whose write tools would otherwise be
    // Unknown and denied below `bypassPermissions`. Caller must consult this ONLY for that preset's package
    // and ONLY absent an explicit hint, so a rogue server reusing these names never gets the fallback.
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

        // Codex names its shell and patch tools differently; same two classes, so it is graded here rather than
        // falling through to Unknown and being denied even where the operator said a shell may run.
        "Bash" or "KillShell" or "KillBash" or "shell" or "command_execution"
            => ToolPermissionClass.Destructive,

        _ => null,
    };

    // Whether `ceiling` lets a delegated session change anything at all (AC-971) — false for `plan`/`default` and for
    // anything unrecognised. Read when judging a finished task's changed-path report: a read-only task that changed
    // files got past a boundary, and that is a failure rather than a result.
    public static bool AllowsChanges(string? ceiling) => _CeilingRank(ceiling) >= 2;

    // Decides whether a delegated session may run `toolName` unattended: allow-listed tools always run,
    // else `toolClass` is graded against `ceiling` (unrecognised = most restrictive). A denial carries a
    // reason for the tool result the model sees — never a hang or a prompt.
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

    // AC-79: the more restrictive of two classes, for a tool name reported by two enabled servers — trust is
    // keyed on the bare name, so a rogue/over-broad server can't shadow a safe name to widen what runs;
    // the worst case wins (ReadOnly &lt; Write &lt; Destructive &lt; Unknown).
    public static ToolPermissionClass MoreRestrictive(ToolPermissionClass a, ToolPermissionClass b) =>
        _Restraint(a) >= _Restraint(b) ? a : b;

    // AC-117: the more restrictive of two ceilings, used to find a per-task ceiling at or below the profile's
    // own (no second consent needed there). A request ABOVE the ceiling is a different, `IsAboveCeiling`-gated
    // path (`DelegationService._EffectiveCeilingAsync`) — escalation is always the operator's call.
    public static string MoreRestrictiveCeiling(string? a, string? b) =>
        _CeilingRank(a) <= _CeilingRank(b) ? a ?? string.Empty : b ?? string.Empty;

    // AC-117: whether `requested` exceeds `ceiling`, triggering operator-elevation consent. A strict rank
    // comparison, not a string one — `default`/`plan` are distinct strings at the same rank, and a string
    // comparison would misprompt for a request that asks no more than the profile already allows.
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
