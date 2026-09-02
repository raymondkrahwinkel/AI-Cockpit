using System.Text;

namespace Cockpit.Plugin.Autopilot;

// The harness's own account of a finished step (AC-255), validated against instead of re-reading the worktree.
// The step agent cannot choose what this says, so a diff's text is data to judge, never instruction (see
// `AutopilotStepBrief.ValidationTurn`). `Concerns`: empty means no spot-check fired, not that the step is correct.
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

        return description.ToString().TrimEnd().ReplaceLineEndings("\n");
    }

    private static string _Bullets(IReadOnlyList<string> paths)
    {
        var listed = string.Join("\n", paths.Take(MaxListedFiles).Select(path => $"- {path}"));
        return paths.Count <= MaxListedFiles
            ? listed
            : $"{listed}\n- … and {paths.Count - MaxListedFiles} more, not listed here";
    }
}
