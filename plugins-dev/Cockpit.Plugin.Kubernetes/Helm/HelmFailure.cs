namespace Cockpit.Plugin.Kubernetes.Helm;

// Turns a failed helm run into something an agent can act on (AC-1061 phase 6). Helm has no fine-grained exit
// codes — everything that goes wrong is 1 — so telling the cases apart means matching stderr text. That is
// best-effort by nature and the tool description says so; the raw stderr always travels with the guess.
internal static class HelmFailure
{
    private const int MaxStderrLength = 800;

    public static string Describe(HelmResult result, string helmExecutablePath)
    {
        if (!result.Started)
        {
            return $"helm could not be started (\"{helmExecutablePath}\"). Install helm, or let the cockpit manage it in the plugin settings.";
        }

        if (result.TimedOut)
        {
            return "helm did not finish in time; nothing was applied.";
        }

        return $"{_Guess(result.Stderr)} helm exited {result.ExitCode}: {_Tail(result.Stderr)}";
    }

    private static string _Guess(string stderr) => stderr switch
    {
        _ when _Has(stderr, "has no deployed releases") || _Has(stderr, "release: not found") =>
            "That release does not exist in this namespace (helm_upgrade never creates one — check helm_list).",
        _ when _Has(stderr, "cluster unreachable") || _Has(stderr, "connection refused") =>
            "The cluster could not be reached.",
        _ when _Has(stderr, "parse error") || _Has(stderr, "execution error") || _Has(stderr, "template:") =>
            "The chart failed to render.",
        _ when _Has(stderr, "not found") =>
            "The chart could not be loaded — check the path or reference.",
        _ => "The helm run failed.",
    };

    private static bool _Has(string stderr, string needle) => stderr.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string _Tail(string stderr)
    {
        var trimmed = stderr.Trim();
        return trimmed.Length <= MaxStderrLength ? trimmed : string.Concat("…", trimmed.AsSpan(trimmed.Length - MaxStderrLength));
    }
}
