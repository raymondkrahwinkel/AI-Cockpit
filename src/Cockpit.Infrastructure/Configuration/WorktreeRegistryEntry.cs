using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="WorktreeRecord"/> under the <c>worktrees</c> section of <c>cockpit.json</c>. A
/// plain DTO kept apart from the domain record so the persisted shape can evolve on its own, mirroring how
/// <see cref="SessionProfileEntry"/> shadows the profile record.
/// </summary>
internal sealed class WorktreeRegistryEntry
{
    public string SessionId { get; set; } = string.Empty;

    public string RepositoryRoot { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string BaseCommit { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsLocked { get; set; }

    public bool IsRetained { get; set; }

    public static WorktreeRegistryEntry FromDomain(WorktreeRecord record) => new()
    {
        SessionId = record.SessionId,
        RepositoryRoot = record.RepositoryRoot,
        Path = record.Path,
        Branch = record.Branch,
        BaseCommit = record.BaseCommit,
        CreatedAt = record.CreatedAt,
        IsLocked = record.IsLocked,
        IsRetained = record.IsRetained,
    };

    public WorktreeRecord ToDomain() => new(SessionId, RepositoryRoot, Path, Branch, BaseCommit, CreatedAt)
    {
        IsLocked = IsLocked,
        IsRetained = IsRetained,
    };
}
