using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Worktrees;

/// <summary>
/// The MCP tools an agent uses to manage its own git worktrees (AC-104, on AC-85), exposed as
/// <c>mcp__cockpit-worktrees__*</c>. Lets a session quickly isolate a subtask on its own branch and clean it up when
/// done, without the operator. Thin over <see cref="IWorktreeManager"/> — the same engine the New-session dialog and
/// the managed-worktrees panel use — so a worktree an agent makes is one the operator also sees, and one the session
/// teardown (AC-85 F3) cleans up if the agent forgets.
/// </summary>
internal sealed class WorktreeTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IWorktreeManager _worktreeManager;
    private readonly ILiveSessionRegistry? _liveSessions;
    private readonly IConsentBroker? _consent;

    // The liveness registry and the consent broker are optional so the tool's own tests construct it without them;
    // the container injects the shared singletons, so a real removal is checked against the running sessions and a
    // dirty removal is gated behind operator consent.
    public WorktreeTools(IWorktreeManager worktreeManager, ILiveSessionRegistry? liveSessions = null, IConsentBroker? consent = null)
    {
        _worktreeManager = worktreeManager;
        _liveSessions = liveSessions;
        _consent = consent;
    }

    [McpServerTool(Name = "worktree_create")]
    [Description("Create a git worktree to isolate a task on its own branch, forked from the current HEAD of the git repository at `directory`. Returns the new worktree's path and branch — run the task's commands with that path. Pass your session id (the COCKPIT_PANE_ID environment variable) as `session` so the worktree is tied to this session and cleaned up when it closes.")]
    public async Task<string> CreateAsync(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("A folder inside the git repository to isolate; the worktree is forked from that repository's current HEAD.")] string directory,
        [Description("Optional branch name; a collision-free one is generated when omitted.")] string? branch = null)
    {
        try
        {
            // Tie the worktree to the transport-verified pane (AC-89/AC-128), not the agent-declared `session`: the
            // owner keys its teardown (CloseSessionAsync releases by pane id), so a forged id would mis-attribute
            // cleanup. Falls back to `session` off the verified path (the in-process tool loop / tests).
            var owner = McpRequestContext.CurrentPaneId ?? session;
            var record = string.IsNullOrWhiteSpace(branch)
                ? await _worktreeManager.CreateForSessionAsync(owner, null, directory)
                : await _worktreeManager.CreateAsync(owner, branch, directory);

            return _Serialize(new { ok = true, path = record.Path, branch = record.Branch });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "worktree_list")]
    [Description("List the git worktrees the cockpit is managing, each with its branch, path, repository, owning session, and git state (clean, whether it has uncommitted changes, and how many commits it is ahead of its base).")]
    public async Task<string> ListAsync()
    {
        var statuses = await _worktreeManager.GetStatusesAsync();
        var worktrees = statuses.Select(status => new
        {
            path = status.Record.Path,
            branch = status.Record.Branch,
            repository = status.Record.RepositoryRoot,
            session = status.Record.SessionId,
            clean = status.IsClean,
            uncommittedChanges = status.HasUncommittedChanges,
            commitsAhead = status.CommitsAhead,
            retained = status.Record.IsRetained,
        });

        return _Serialize(new { ok = true, worktrees });
    }

    [McpServerTool(Name = "worktree_remove")]
    [Description("Remove a git worktree the cockpit created — for example when a task is done. A clean worktree is removed right away; a worktree that still holds uncommitted changes or untracked files is removed only after the operator approves a consent prompt (which discards them — any committed history stays on the branch). Refuses a worktree a live session is still running in. Use worktree_list to get the path.")]
    public async Task<string> RemoveAsync(
        [Description("The worktree's path, as returned by worktree_create or worktree_list.")] string path)
    {
        var full = Path.GetFullPath(path);
        var record = (await _worktreeManager.ListAsync())
            .FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate.Path), full, _PathComparison));
        if (record is null)
        {
            return _Serialize(new { ok = false, error = "No managed worktree at that path — call worktree_list for the current paths." });
        }

        // Never remove a worktree whose owning session is still running — that pulls the working directory out from
        // under it. The panel enforces the same guard; close the session first, or let its own teardown remove it.
        if (_liveSessions is not null && _liveSessions.LiveSessionIds.Contains(record.SessionId))
        {
            return _Serialize(new { ok = false, error = "That worktree's session is still running — it will be cleaned up when the session closes; do not remove a live session's worktree." });
        }

        // A worktree that still holds uncommitted changes or untracked files only comes out after the operator
        // approves it: force-removing it discards that content, so the human decides, not the agent. The broker fails
        // closed (no operator surface, no approval), so a headless or delegated agent can never discard work this way.
        // A clean worktree (or one that only has commits ahead, which the removal keeps on the branch) needs no prompt.
        // The prompt is not pinned to a pane: the caller is untrusted (an agent-declared id), so it is shown to the
        // operator unattributed rather than trusting the agent to say which session is asking.
        var dirty = await _worktreeManager.HasUncommittedChangesAsync(record);
        if (dirty)
        {
            if (_consent is null)
            {
                return _Serialize(new { ok = false, error = "This worktree still holds uncommitted changes or untracked files; removing it needs the operator's approval, which is not available here." });
            }

            var decision = await _consent.RequestConsentAsync(new ConsentRequest(
                "An agent wants to remove a worktree with unsaved changes",
                $"Remove worktree {_SingleLine(record.Path)}\nbranch {_SingleLine(record.Branch)}\nThis discards its uncommitted changes and untracked files. Any committed history stays on the branch.",
                new ConsentSource(null, null, "Worktrees MCP"),
                "worktree.remove.dirty",
                ConsentRisk.Dangerous));
            if (!decision.IsApproved)
            {
                return _Serialize(new { ok = false, error = "Removing a worktree with unsaved changes was not approved by the operator." });
            }
        }

        try
        {
            await _worktreeManager.RemoveAsync(record, force: dirty);
            return _Serialize(new { ok = true });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    private static readonly StringComparison _PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Fold any character a consent surface could render as a line break out of an agent-influenced value (the branch
    // name it chose at worktree_create) before it goes verbatim into the Dangerous prompt's Action. git rejects ASCII
    // control characters in a ref but not the Unicode line/paragraph separators (U+2028/U+2029/U+0085), which an agent
    // could otherwise use to smuggle in reassuring extra lines and bury the "this discards your changes" warning
    // (cf. AC-80, which flattens control characters in a consent Action for the same reason).
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029' or '\u0085' ? ' ' : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
