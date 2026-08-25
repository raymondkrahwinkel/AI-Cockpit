using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Consent;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Worktrees;

// The MCP tools an agent uses to manage its own git worktrees (AC-104, on AC-85), exposed as
// `mcp__cockpit-worktrees__*`. Thin over `IWorktreeManager` — the same engine the New-session dialog and the
// managed-worktrees panel use — so an agent-made worktree is one the operator also sees.
internal sealed class WorktreeTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IWorktreeManager _worktreeManager;
    private readonly ILiveSessionRegistry? _liveSessions;
    private readonly IConsentBroker? _consent;

    // Liveness registry and consent broker are optional so tests construct this without them; the container
    // injects the shared singletons for real removals. Absent liveness is not "assume dead" — the cross-session
    // guard in RemoveAsync stays categorical.
    public WorktreeTools(IWorktreeManager worktreeManager, ILiveSessionRegistry? liveSessions = null, IConsentBroker? consent = null)
    {
        _worktreeManager = worktreeManager;
        _liveSessions = liveSessions;
        _consent = consent;
    }

    [McpServerTool(Name = "worktree_create", ReadOnly = false, Destructive = false)]
    [Description("Create a git worktree to isolate a task on its own branch. The source branch is fetched and fast-forwarded first where that is safe, so the worktree starts on the latest state of the repository at `directory` rather than on whatever was last pulled. Returns the new worktree's path and branch — run the task's commands with that path — plus `sourceNotice` when the fork base is not the latest (offline, uncommitted changes, or a diverged branch). Pass your session id (the COCKPIT_PANE_ID environment variable) as `session` so the worktree is tied to this session and cleaned up when it closes. Marked as made through this tool, so worktree_remove lets you clean it up yourself later — for example when the task is done — even while your own session is still running; that is unlike the worktree your session runs in, which stays off limits to worktree_remove no matter who asks.")]
    public async Task<string> CreateAsync(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("A folder inside the git repository to isolate; the worktree is forked from that repository's source branch, brought up to date first where possible.")] string directory,
        [Description("Optional branch name; a collision-free one is generated when omitted.")] string? branch = null)
    {
        try
        {
            // Tie the worktree to the transport-verified pane (AC-89/AC-128), not the agent-declared `session`: the
            // owner keys its teardown (CloseSessionAsync releases by pane id), so a forged id would mis-attribute
            // cleanup. Falls back to `session` off the verified path (the in-process tool loop / tests).
            var owner = McpRequestContext.CurrentPaneId ?? session;

            // LeaveSourceAlone, always: `directory`'s own branch/working tree are never written to on an agent's
            // say-so (AC-376), though the worktree still forks from the upstream tip. isAgentCreated: true
            // (AC-520 fix 5) marks it as agent-made so its owning session may remove it later even while live.
            var record = string.IsNullOrWhiteSpace(branch)
                ? await _worktreeManager.CreateForSessionAsync(owner, null, directory, WorktreeSourceHandling.LeaveSourceAlone, isAgentCreated: true)
                : await _worktreeManager.CreateAsync(owner, branch, directory, WorktreeSourceHandling.LeaveSourceAlone, isAgentCreated: true);

            // The notice rides along only when there is one (AC-349): an agent that reads "forked from your local
            // main, 30 commits behind" can say so instead of quietly building on a base nobody meant it to have.
            return _Serialize(new
            {
                ok = true,
                path = record.Path,
                branch = record.Branch,
                sourceNotice = record.SourceRefresh?.Notice,
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "worktree_list", ReadOnly = true)]
    [Description("List the git worktrees the cockpit is managing, each with its branch, path, repository, owning session, and git state (clean, whether it has uncommitted changes, and how many commits exist only here — not in the base branch and not pushed anywhere). `ownerLive` tells apart the three things a bare `session` id cannot: true with an ordinary session id means that session is still running (never remove or reassign it); false means the owner is gone and any work is only kept if `retained` says so; true with `session` equal to `cockpit-assistant` means the assistant owns it directly — the assistant is always live by construction, so this stays true until worktree_handover or worktree_remove moves it, never because a sweep missed it. Null when liveness cannot be determined here.")]
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
            commitsOnlyHere = status.StrandableCommits,
            retained = status.Record.IsRetained,
            // AC-719: the same liveness question WorktreesViewModel.RefreshAsync already answers for the panel,
            // reused rather than re-derived, so an MCP caller can tell "owner still running" apart from "owner
            // gone, work retained" instead of guessing from `session`/`retained` alone.
            ownerLive = _liveSessions is null ? (bool?)null : _liveSessions.LiveSessionIds.Contains(status.Record.SessionId),
        });

        return _Serialize(new { ok = true, worktrees });
    }

    [McpServerTool(Name = "worktree_remove", ReadOnly = false, Destructive = true)]
    [Description("Remove a git worktree the cockpit created — for example when a task is done. A clean worktree is removed right away; a worktree that still holds uncommitted changes or untracked files is removed only after the operator approves a consent prompt (which discards them — any committed history stays on the branch). You may remove a worktree worktree_create made for you even while your own session is still running — that is exactly the case this tool exists for. Refused either way: the worktree your own session actually runs in (made when the session started or was reattached, never through this tool), and any worktree owned by a *different* session that is still live — that cross-session cleanup is the operator's, from the managed-worktrees panel, not an agent's. The response carries `notice` when the removal left something behind that the repository could no longer be asked about — its worktree folder is never deleted in that case, only untracked from the cockpit — worth relaying rather than treating as a bare success. Use worktree_list to get the path.")]
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

        // AC-128: an agent may only remove a worktree it owns — cross-session cleanup while the owner is alive is
        // the operator's, from the managed panel. Once the owner is provably dead (absent from LiveSessionIds) this
        // loosens, since refusing only leaves an orphan (AC-524); with no registry to ask, the guard stays categorical.
        if (McpRequestContext.CurrentPaneId is { } caller
            && !string.Equals(record.SessionId, caller, StringComparison.Ordinal)
            && (_liveSessions is null || _liveSessions.LiveSessionIds.Contains(record.SessionId)))
        {
            return _Serialize(new { ok = false, error = "That worktree belongs to another session — you can only remove a worktree you created." });
        }

        // Never remove a worktree whose owning session is still running — catches an agent removing its own live
        // worktree, which the guard above doesn't (same session). Exception (AC-520 fix 5): one it made for itself
        // via worktree_create is exempt, told apart by IsAgentCreated.
        var removingOwnAgentMadeWorktree =
            record.IsAgentCreated && string.Equals(record.SessionId, McpRequestContext.CurrentPaneId, StringComparison.Ordinal);

        if (_liveSessions is not null && _liveSessions.LiveSessionIds.Contains(record.SessionId) && !removingOwnAgentMadeWorktree)
        {
            return _Serialize(new { ok = false, error = "That worktree's session is still running — it will be cleaned up when the session closes; do not remove a live session's worktree." });
        }

        // A dirty worktree only comes out after operator approval — force-removing discards content, so the human
        // decides, not the agent. The broker fails closed with no operator surface. Not pinned to a pane: the
        // caller is untrusted, so the prompt shows unattributed.
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
                new ConsentSource(null, null, ConsentSourceCatalog.WorktreesMcp),
                "worktree.remove.dirty",
                ConsentRisk.Dangerous));
            if (!decision.IsApproved)
            {
                return _Serialize(new { ok = false, error = "Removing a worktree with unsaved changes was not approved by the operator." });
            }
        }

        try
        {
            var notice = await _worktreeManager.RemoveAsync(record, force: dirty);
            return _Serialize(new { ok = true, notice });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    private static readonly StringComparison _PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Strip characters a consent surface could render as a line break from an agent-influenced value (the branch
    // name) before it goes into the Dangerous prompt's Action — git allows Unicode line separators an agent could
    // use to bury the warning (cf. AC-80).
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029' or '\u0085' ? ' ' : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
