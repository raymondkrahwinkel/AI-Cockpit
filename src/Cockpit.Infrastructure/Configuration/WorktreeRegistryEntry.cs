using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `WorktreeRecord` under the `worktrees` section of `cockpit.json`. A
// plain DTO kept apart from the domain record so the persisted shape can evolve on its own, mirroring how
// `SessionProfileEntry` shadows the profile record.
internal sealed class WorktreeRegistryEntry
{
    public string SessionId { get; set; } = string.Empty;

    public string RepositoryRoot { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string BaseCommit { get; set; } = string.Empty;

    // The branch the worktree forked from, when known; absent on entries written before this was tracked (they deserialize to null and the status check falls back to detecting the default branch).
    public string? BaseBranch { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsLocked { get; set; }

    public bool IsRetained { get; set; }

    // Whether an agent made this worktree through the MCP tool (AC-520 fix 5); absent on entries written before this was tracked, which deserialize to false — read as "session-own, protected", the safe default.
    public bool IsAgentCreated { get; set; }

    public static WorktreeRegistryEntry FromDomain(WorktreeRecord record) => new()
    {
        SessionId = record.SessionId,
        RepositoryRoot = record.RepositoryRoot,
        Path = record.Path,
        Branch = record.Branch,
        BaseCommit = record.BaseCommit,
        BaseBranch = record.BaseBranch,
        CreatedAt = record.CreatedAt,
        IsLocked = record.IsLocked,
        IsRetained = record.IsRetained,
        IsAgentCreated = record.IsAgentCreated,
    };

    public WorktreeRecord ToDomain() => new(SessionId, RepositoryRoot, Path, Branch, BaseCommit, CreatedAt)
    {
        BaseBranch = BaseBranch,
        IsLocked = IsLocked,
        IsRetained = IsRetained,
        IsAgentCreated = IsAgentCreated,
    };
}
