namespace Cockpit.Infrastructure.Worktrees;

/// <summary>The exit code and captured streams of one git invocation. Kept whole so callers can read an exit code without treating a non-zero as an exception when a non-zero is the answer (a folder that is simply not a repository).</summary>
internal readonly record struct GitResult(int ExitCode, string StandardOutput, string StandardError);
