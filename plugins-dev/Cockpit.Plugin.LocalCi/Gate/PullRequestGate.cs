using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Gate;

/// <summary>
/// What the gate says about a checkout. Four answers rather than a boolean, because the difference between "there
/// is a green run for this commit" and "nothing ran" is the whole point: this project has been bitten before by a
/// guard that guarded nothing and stood green while doing it.
/// </summary>
internal enum GateStatus
{
    /// <summary>The gate is not switched on for this checkout. Nothing to say.</summary>
    Off,

    /// <summary>A local run passed, on the commit that is checked out now.</summary>
    Passed,

    /// <summary>The last local run failed.</summary>
    Failed,

    /// <summary>Nothing ran, or what ran reached no verdict, or it ran on a different commit.</summary>
    NotRun,
}

/// <param name="Reason">Completes "the pull request was held back because …". Empty when the gate is off or passed.</param>
internal sealed record GateVerdict(GateStatus Status, string Reason)
{
    /// <summary>
    /// Whether a pull request may go ahead without asking anybody. Only two of the four answers qualify, and
    /// <see cref="GateStatus.NotRun"/> is deliberately not one of them.
    /// </summary>
    public bool AllowsWithoutAsking => Status is GateStatus.Off or GateStatus.Passed;

    public static GateVerdict Off { get; } = new(GateStatus.Off, string.Empty);

    public static GateVerdict Passed { get; } = new(GateStatus.Passed, string.Empty);
}

/// <summary>
/// Answers whether a checkout has earned a pull request. Off unless the operator switched it on for that checkout,
/// and it never invents a pass: a run that could not happen is "did not run", which is a different answer from
/// "passed" and has to be treated as one by whoever asks.
/// </summary>
internal sealed class PullRequestGate(LocalRunTracker tracker, PullRequestGateSettings settings, GitHead head)
{
    public async Task<GateVerdict> JudgeAsync(string checkout, CancellationToken cancellationToken)
    {
        if (!settings.IsOnFor(checkout))
        {
            return GateVerdict.Off;
        }

        if (tracker.LastFor(checkout) is not { } last)
        {
            return new GateVerdict(GateStatus.NotRun, "nothing has been run on this machine in this checkout yet.");
        }

        if (last.Result.Outcome == LocalRunOutcome.Failed)
        {
            return new GateVerdict(GateStatus.Failed, last.Result.Headline);
        }

        if (last.Result.Outcome != LocalRunOutcome.Passed)
        {
            return new GateVerdict(GateStatus.NotRun, last.Result.Headline);
        }

        var now = await head.ReadAsync(checkout, cancellationToken);

        // Both halves have to be known. An unreadable HEAD, or a run recorded before the commit could be read, is
        // not evidence that the run was about this code — and "we could not check" must not read as "we checked".
        if (now is null || last.Commit is null)
        {
            return new GateVerdict(
                GateStatus.NotRun,
                "the last local run passed, but which commit it ran on could not be established.");
        }

        return string.Equals(now, last.Commit, StringComparison.Ordinal)
            ? GateVerdict.Passed
            : new GateVerdict(
                GateStatus.NotRun,
                $"the last local run passed on {_Short(last.Commit)}, and this checkout is now on {_Short(now)}.");
    }

    private static string _Short(string commit) => commit.Length <= 8 ? commit : commit[..8];
}
