using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Shell;

namespace Cockpit.Infrastructure.Tests.Shell;

public class TrackedCommandRunnerTests : IDisposable
{
    private readonly DirectoryInfo _workingDirectory = Directory.CreateTempSubdirectory("cockpit-tracked-runner-test-");
    private readonly TrackedCommandRunner _runner = new(NullLogger<TrackedCommandRunner>.Instance);

    public void Dispose() => _workingDirectory.Delete(recursive: true);

    private Task<TrackedRunResult> _RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout) =>
        _runner.RunAsync(_workingDirectory.FullName, command, arguments, timeout, Guid.NewGuid().ToString("N"));

    [PosixFact("Not yet covered on Windows rather than inapplicable: there the metacharacter would be `&`, not `;`, and nothing echoes argv verbatim without a shell to prove it against; the ArgumentList path itself is covered there by RunAsync_RunsTheCommand_AndReportsExitCodeAndStdout.")]
    public async Task RunAsync_NeverLetsAShellReparseAnArgument()
    {
        var result = await _RunAsync("echo", ["a; rm -rf poisoned"], TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("a; rm -rf poisoned", result.StandardOutput);
        Assert.False(Directory.Exists(Path.Combine(_workingDirectory.FullName, "poisoned")));
    }

}
