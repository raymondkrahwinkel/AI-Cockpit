namespace Cockpit.Core.Worktrees;

// A missing git executable is different from a folder that is not a repository: a session can still run there.
public sealed class GitUnavailableException(Exception innerException)
    : InvalidOperationException("Git is not installed or is unavailable on PATH.", innerException);
