namespace Cockpit.Core.Abstractions.Shell;

/// <summary>
/// Runs one command as a child process (AC-1066) and returns its <see cref="ShellCommandResult"/>. A seam of its
/// own so <c>ShellMcpTools</c> can be tested without spawning a real process.
/// </summary>
public interface IShellCommandRunner
{
    Task<ShellCommandResult> RunAsync(
        string workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
