using System.Text;

namespace Cockpit.Plugin.Autopilot;

// The harness's own account of a finished step (AC-255) — what it observed, and anything about that observation worth
// a closer look. This is what the CEO validates against instead of re-reading the whole worktree itself. It is
// composed here, from what `IAutopilotEvidenceSource` saw, and the agent's own summary is carried
// separately in the validation turn so the two can never be mistaken for each other.
//
// The step agent does not write this account and cannot choose what it says. It does, of course, write the code the
// account describes — so the text inside a diff is the step's own output and is handed to the CEO as data to judge,
// never as instruction (see `AutopilotStepBrief.ValidationTurn`, which fences it for exactly that reason).
//
// `Commit`:
// The commit this observation was measured on (AC-1037) — carried as its own field rather than left inside the
// prose, so the validation turn cannot render an observation without saying which tree it is of.
// `Observation`: What the harness saw, already worded for the validation turn.
// `Concerns`:
// What the harness flagged about the change (`AutopilotEvidenceSignals`). Empty means no spot-check fired
// — not that the step is correct, and the turn says so in as many words.
internal sealed record AutopilotStepEvidence(string Commit, string Observation, IReadOnlyList<string> Concerns)
{
    // A path list is as unbounded as a diff is, and for the same reason it has to be capped: a wide refactor — or a
    // repo whose build output is not ignored — would otherwise blow past the budget the patch cap exists to enforce.
    private const int MaxListedFiles = 50;

    public static AutopilotStepEvidence From(AutopilotWorktreeChange change, AutopilotStep step, IReadOnlyList<string> summaries) =>
        new(change.HeadCommit, _Describe(change), AutopilotEvidenceSignals.For(change, step, summaries));

    private static string _Describe(AutopilotWorktreeChange change)
    {
        if (change.IsEmpty)
        {
            return "The run's worktree is unchanged since this step started: no file was modified, and no new file was added.";
        }

        var description = new StringBuilder();

        if (change.FilesChanged.Count > 0)
        {
            description.AppendLine($"Files changed ({change.FilesChanged.Count}):");
            description.AppendLine(_Bullets(change.FilesChanged));
        }

        if (change.UntrackedFiles.Count > 0)
        {
            description.AppendLine($"New files, not yet added to git ({change.UntrackedFiles.Count}) — a diff cannot show their contents:");
            description.AppendLine(_Bullets(change.UntrackedFiles));
        }

        if (!string.IsNullOrWhiteSpace(change.Patch))
        {
            description.AppendLine("Diff:");
            description.AppendLine(change.Patch.TrimEnd());
        }

        if (change.Truncated)
        {
            description.AppendLine();
            description.AppendLine("This diff was longer than this turn carries and was cut off — read the files themselves for the rest.");
        }

        return description.ToString().TrimEnd();
    }

    private static string _Bullets(IReadOnlyList<string> paths)
    {
        var listed = string.Join("\n", paths.Take(MaxListedFiles).Select(path => $"- {path}"));
        return paths.Count <= MaxListedFiles
            ? listed
            : $"{listed}\n- … and {paths.Count - MaxListedFiles} more, not listed here";
    }
}
