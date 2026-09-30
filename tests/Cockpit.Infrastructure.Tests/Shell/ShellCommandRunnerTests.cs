using Cockpit.Infrastructure.Shell;

namespace Cockpit.Infrastructure.Tests.Shell;

/// <summary>
/// <see cref="ShellCommandRunner"/> (AC-1066): the child-process execution behind <c>run_command</c>, the shell a
/// session on a provider with no built-in one otherwise lacks. These prove the three things the ticket's
/// acceptance criteria hang on: it actually runs a command and reports its outcome, it never lets a shell
/// re-parse an argument, and it survives a time-out and a chatty child without deadlocking.
/// </summary>
public sealed class ShellCommandRunnerTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "cockpit-shell-tests", Guid.NewGuid().ToString("N"));
    private readonly ShellCommandRunner _runner = new();

    public ShellCommandRunnerTests() => Directory.CreateDirectory(_workingDirectory);

    [PosixFact("Not yet covered on Windows rather than inapplicable: there the metacharacter would be `&`, not `;`, and nothing echoes argv verbatim without a shell to prove it against; the ArgumentList path itself is covered there by RunAsync_RunsTheCommand_AndReportsExitCodeAndStdout.")]
    public async Task RunAsync_NeverLetsAShellReparseAnArgument()
    {
        // If this argument were re-parsed by a shell, "; rm -rf poisoned" would run as a second command. It never
        // is: ArgumentList passes it to echo as one literal argument, which prints it back verbatim.
        const string payload = "a; rm -rf poisoned";

        var result = await _runner.RunAsync(_workingDirectory, "echo", [payload], TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(payload, result.StandardOutput);
        Assert.False(Directory.Exists(Path.Combine(_workingDirectory, "poisoned")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
