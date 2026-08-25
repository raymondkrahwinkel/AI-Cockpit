using Cockpit.Plugin.Kubernetes.Cli;

namespace Cockpit.Plugin.Kubernetes.Kind;

// Turns a failed kind run into something an agent can act on, mirroring Helm/HelmFailure.cs: kind has no
// fine-grained exit codes for these cases either, so telling them apart means matching stderr text — best-effort by
// nature, and the raw stderr always travels with the guess.
internal static class KindFailure
{
    private const int MaxStderrLength = 800;

    public static string Describe(CliResult result, string kindExecutablePath)
    {
        if (!result.Started)
        {
            return $"kind could not be started (\"{kindExecutablePath}\"). Install it — see the kind runtime status for platform-specific instructions.";
        }

        if (result.TimedOut)
        {
            return "kind did not finish in time; the cluster may be partially created — check `kind get clusters` before retrying with the same name.";
        }

        return $"{_Guess(result.Stderr)} kind exited {result.ExitCode}: {_Tail(result.Stderr)}";
    }

    private static string _Guess(string stderr) => stderr switch
    {
        _ when _Has(stderr, "already exist") =>
            "A cluster with that name already exists (check kind_list).",
        _ when _Has(stderr, "Cannot connect to the Docker daemon") || _Has(stderr, "docker: command not found") || _Has(stderr, "no provider found") =>
            "No container runtime could be reached — kind needs Docker or Podman running.",
        _ when _Has(stderr, "context deadline exceeded") || _Has(stderr, "timed out waiting") =>
            "The cluster's control plane did not come up in time.",
        _ => "The kind run failed.",
    };

    private static bool _Has(string stderr, string needle) => stderr.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string _Tail(string stderr)
    {
        var trimmed = stderr.Trim();
        return trimmed.Length <= MaxStderrLength ? trimmed : string.Concat("…", trimmed.AsSpan(trimmed.Length - MaxStderrLength));
    }
}
