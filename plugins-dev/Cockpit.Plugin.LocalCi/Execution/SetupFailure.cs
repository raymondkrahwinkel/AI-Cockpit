namespace Cockpit.Plugin.LocalCi.Execution;

// Tells a run that failed *on the project* apart from one that fell over before the project was ever
// reached (AC-617).
//
// **Why this is worth its own answer.** A job has two halves: the setup act does for it — bringing the
// container up, fetching the `uses:` actions the classifier allows — and the `run:` steps that are this
// project's own. Only the second half says anything about the code. Reporting the first as "build failed" is a
// claim about a diff that was never compiled, and it is expensive in exactly the moment it is made: the operator
// reads red, distrusts a change that is fine, and goes looking in the wrong place. It happened for real with
// Docker Engine 29.7.0, whose `CopyToContainer` refused any path traversing the `/var/run → /run`
// symlink — every job on the machine went red in six seconds, in the setup, with a daemon message as the only
// clue (moby/moby#53258, fixed in 29.7.1).
//
// **Why it is not a list of broken versions.** That was the tempting fix and it is the wrong one: the same
// class of regression had already shipped once before (29.5.0, fixed in 29.5.2) and a list only ever knows about
// the breakage that has already cost someone an afternoon. What generalises is the shape — did the run get as far
// as this project's own work? — and that question has the same answer for a daemon regression, an image that
// cannot be pulled, a proxy eating the action download, or whatever breaks next.
internal static class SetupFailure
{
    // How act announces a step that did not survive. Its own UI format, matched rather than parsed: the step name
    // follows on the same line, which is all this needs.
    private const string FailureMarker = "Failure - ";

    // act's name for bringing the container up, before any step of the workflow runs at all. Not a step anyone
    // wrote, so it can never be this project's.
    private const string JobSetupStep = "Set up job";

    // The reason a failed run is not a verdict on the code, or null when it is one.
    //
    // The judgement is made against `setupActions` — the `uses:` references this job
    // actually declares, read from the workflow rather than guessed from the shape of a name. A `run:` step
    // is free to be called anything, `owner/repo@v1` included, so recognising a setup step by "it looks like
    // an action reference" would let a project step be dismissed as somebody else's problem. Reading the job is
    // both cheaper and exact.
    // `output`: The run's output, in the order act produced it.
    // `setupActions`: This job's `uses:` references, e.g. `actions/checkout@v7`.
    public static string? Reason(IEnumerable<string> output, IReadOnlyCollection<string> setupActions)
    {
        string? failedAt = null;
        foreach (var line in output)
        {
            var marker = line.IndexOf(FailureMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var step = line[(marker + FailureMarker.Length)..].Trim();

            // The first failure is the one that ended the job — act stops there. Everything after it is the
            // aftermath (a "Job failed" line, a cleanup step), and reading a later one would describe the tidying
            // up rather than the thing that went wrong.
            failedAt ??= step;
        }

        if (failedAt is null)
        {
            // act reported a non-zero exit with no step failure in the output at all: act itself did not get off
            // the ground (a bad argument, a workflow it would not read). Not the project's doing either, but this
            // is not the place to name it — the caller's own "could not be started" answers that case.
            return null;
        }

        if (failedAt.Contains(JobSetupStep, StringComparison.Ordinal))
        {
            return "it never got past setting the job up, so nothing here is about the code. "
                + "The log tail says what the container engine reported.";
        }

        var action = setupActions.FirstOrDefault(used => failedAt.Contains(used, StringComparison.Ordinal));
        return action is null
            ? null
            : $"it failed while fetching {action}, before a single step of this project ran — so this says nothing "
                + "about the code. The log tail has what went wrong; a container engine or a network that cannot "
                + "hand over an action is the usual cause.";
    }
}
