using Cockpit.Core.WorkingPaths;

namespace Cockpit.Core.Worktrees;

/// <summary>Finding a registered worktree by the folder it occupies — the registry read every "is this folder one of ours" question goes through.</summary>
public static class WorktreeLookup
{
    /// <summary>
    /// The registered worktree whose folder is exactly <paramref name="directory"/>, or <see langword="null"/> when
    /// no record matches. Exactly the folder rather than anything inside it: a worktree is a checkout, and a
    /// sub-folder of one is not a worktree the host owns.
    /// </summary>
    public static WorktreeRecord? At(IEnumerable<WorktreeRecord> worktrees, string? directory)
    {
        if (DirectoryPath.Normalize(directory) is not { } target)
        {
            return null;
        }

        return worktrees.FirstOrDefault(record => string.Equals(DirectoryPath.Normalize(record.Path), target, DirectoryPath.Comparison));
    }
}
