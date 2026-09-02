using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Tests;

// `HelmRunner` only translates `CliRunner` (AC-179), so the process behaviour — missing executable, deadline,
// locked environment — is proven once in `CliRunnerTests` against the code that implements it. Left here is the
// translation: `HelmResult` is built positionally, so a swapped pair reads a clean run as timed out.
public class HelmRunnerTests
{
    [Fact]
    public async Task ARunThatSucceeded_ArrivesAsAHelmResultWithEveryFieldOnTheRightOne()
    {
        // Both streams written on a clean exit, because helm 4.2.2 does exactly that — deprecation warnings and
        // NOTES.txt go to stderr while the payload goes to stdout, and neither may end up in the other's field or
        // flip a zero exit code to a failure.
        var (fileName, arguments) = _ShellCommand("echo the-payload && echo deprecation-warning 1>&2");
        var command = new HelmCommand(fileName, arguments, new Dictionary<string, string>());

        var result = await new HelmRunner().RunAsync(command, TimeSpan.FromSeconds(30));

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains("the-payload", result.Stdout);
        Assert.Contains("deprecation-warning", result.Stderr);
    }

    private static (string FileName, string[] Arguments) _ShellCommand(string script) =>
        OperatingSystem.IsWindows() ? ("cmd", ["/c", script]) : ("sh", ["-c", script]);
}
