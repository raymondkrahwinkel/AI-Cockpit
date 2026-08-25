using Cockpit.Plugin.Kubernetes.Cli;
using Cockpit.Plugin.Kubernetes.Kind;

namespace Cockpit.Plugin.Kubernetes.Tests;

// Turning a failed kind run into an agent-facing message (AC-179), mirroring HelmFailureTests' shape: best-effort
// stderr string-matching, with the raw stderr always attached so a wrong guess never hides the real reason.
public class KindFailureTests
{
    [Fact]
    public void NotStarted_ReportsInstallInstructions()
    {
        var message = KindFailure.Describe(CliResult.NotStarted, "kind");

        Assert.Contains("could not be started", message);
    }

    [Fact]
    public void TimedOut_ReportsNothingWasApplied()
    {
        var message = KindFailure.Describe(CliResult.Timeout, "kind");

        Assert.Contains("did not finish in time", message);
    }

    [Fact]
    public void NameCollision_IsGuessedFromStderr()
    {
        var result = CliResult.Exited(1, string.Empty, "ERROR: failed to create cluster: node(s) already exist for a cluster with the name \"cockpit-ac179\"");

        var message = KindFailure.Describe(result, "kind");

        Assert.Contains("already exists", message);
        Assert.Contains("already exist for a cluster", message);
    }

    [Fact]
    public void NoContainerRuntime_IsGuessedFromStderr()
    {
        var result = CliResult.Exited(1, string.Empty, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?");

        var message = KindFailure.Describe(result, "kind");

        Assert.Contains("container runtime", message);
    }

    [Fact]
    public void UnrecognisedFailure_FallsBackToGenericGuessWithStderrTail()
    {
        var result = CliResult.Exited(1, string.Empty, "some unexpected kind failure text");

        var message = KindFailure.Describe(result, "kind");

        Assert.Contains("exited 1", message);
        Assert.Contains("some unexpected kind failure text", message);
    }
}
