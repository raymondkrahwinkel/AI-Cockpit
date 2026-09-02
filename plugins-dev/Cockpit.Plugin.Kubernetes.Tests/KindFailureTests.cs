using Cockpit.Plugin.Kubernetes.Cli;
using Cockpit.Plugin.Kubernetes.Kind;

namespace Cockpit.Plugin.Kubernetes.Tests;

// Turning a failed kind run into an agent-facing message (AC-179), mirroring HelmFailureTests' shape: best-effort
// stderr string-matching, with the raw stderr always attached so a wrong guess never hides the real reason.
// `CliResult` is internal, so the rows box each result and the test casts it back once.
public class KindFailureTests
{
    public static IEnumerable<object[]> Failures() =>
    [
        [CliResult.NotStarted, new[] { "could not be started" }],
        [CliResult.Timeout, new[] { "did not finish in time" }],
        [
            CliResult.Exited(1, string.Empty, "ERROR: failed to create cluster: node(s) already exist for a cluster with the name \"cockpit-ac179\""),
            new[] { "already exists", "already exist for a cluster" },
        ],
        [
            CliResult.Exited(1, string.Empty, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?"),
            new[] { "container runtime" },
        ],
        // No guess fits, so the message says what kind did and hands over the stderr it was given.
        [
            CliResult.Exited(1, string.Empty, "some unexpected kind failure text"),
            new[] { "exited 1", "some unexpected kind failure text" },
        ],
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public void Describe_GuessesTheReason_AndNeverHidesWhatKindActuallySaid(object result, string[] expected)
    {
        var message = KindFailure.Describe((CliResult)result, "kind");

        Assert.All(expected, fragment => Assert.Contains(fragment, message));
    }
}
