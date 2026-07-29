using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>
/// The commit a checkout is on. Recorded with every run so a later "it passed here" can be checked against the code
/// it passed on — a green run from three commits ago is not an answer about the code you are about to push, and a
/// gate that cannot tell the difference is a gate that waves everything through.
/// </summary>
internal sealed class GitHead(ICliRunner runner)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The checkout's HEAD, or null when git is absent or this is not a repository — an unknown commit
    /// is a fact to carry, not a reason to fail the run that was about to happen.</summary>
    public async Task<string?> ReadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "git",
            ["-C", projectRoot, "rev-parse", "HEAD"],
            ReadTimeout,
            cancellationToken);

        if (!result.Succeeded)
        {
            return null;
        }

        var head = result.StandardOutput.Trim();
        return head.Length == 0 ? null : head;
    }
}
