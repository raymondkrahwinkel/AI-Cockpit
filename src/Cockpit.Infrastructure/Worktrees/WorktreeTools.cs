using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Worktrees;

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

    public WorktreeTools(IWorktreeManager worktreeManager)
    {
        _worktreeManager = worktreeManager;
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
            var record = string.IsNullOrWhiteSpace(branch)
                ? await _worktreeManager.CreateForSessionAsync(session, null, directory)
                : await _worktreeManager.CreateAsync(session, branch, directory);

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
    [Description("Remove a git worktree the cockpit created — for example when a task is done. Refuses a worktree with uncommitted changes unless `force` is true (which discards those changes; any committed history stays on the branch). Use worktree_list to get the path. Do not remove the worktree your own session is running in.")]
    public async Task<string> RemoveAsync(
        [Description("The worktree's path, as returned by worktree_create or worktree_list.")] string path,
        [Description("Discard uncommitted changes to remove a dirty worktree. Default false.")] bool force = false)
    {
        var full = Path.GetFullPath(path);
        var record = (await _worktreeManager.ListAsync())
            .FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate.Path), full, _PathComparison));
        if (record is null)
        {
            return _Serialize(new { ok = false, error = "No managed worktree at that path — call worktree_list for the current paths." });
        }

        try
        {
            await _worktreeManager.RemoveAsync(record, force);
            return _Serialize(new { ok = true });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    private static readonly StringComparison _PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
