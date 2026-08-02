using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>
/// Removes what a stopped run left behind. act cleans up after itself when it finishes; killed halfway it does not,
/// and a container that outlives the run holds the cores the operator stopped the run to get back.
/// </summary>
internal interface IRunContainerCleanup
{
    Task RemoveAsync(string runId, CancellationToken cancellationToken);
}

// Finds a run's leftovers by the label every one of its containers carries, and forces them away.
//
// By label rather than by name: act names containers after the workflow and job, so two projects running the same
// job would clean up each other's containers — and a label is the only thing here that is unique to one run.
internal sealed class DockerRunCleanup(ICliRunner runner) : IRunContainerCleanup
{
    // Long enough for a busy engine, short enough that stopping a run never appears to hang.
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(20);

    public async Task RemoveAsync(string runId, CancellationToken cancellationToken)
    {
        var listed = await runner.RunAsync(
            "docker",
            ["ps", "-aq", "--filter", $"label={ActRunOptions.RunLabel}={runId}"],
            CleanupTimeout,
            cancellationToken);

        if (!listed.Succeeded)
        {
            return;
        }

        var containers = listed.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (containers.Count == 0)
        {
            return;
        }

        await runner.RunAsync("docker", ["rm", "-f", .. containers], CleanupTimeout, cancellationToken);
    }
}
