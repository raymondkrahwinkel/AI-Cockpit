namespace Cockpit.Core.Projects;

// AC-491: one piece of work a project offers to start — a saved prompt, and a line saying out loud what it
// changes, writes or sends ("changes nothing · reports only"). Nothing more: a job is not a second kind of
// session, only the text that would otherwise have to be typed into an empty box.
public sealed record ProjectJob(string Prompt, string BlastRadius)
{
    // Whether this row says nothing yet — an untouched row the editor added and the operator left alone. Dropped
    // on save rather than kept, the same as `ProjectInfoField.IsBlank`.
    public bool IsBlank => string.IsNullOrWhiteSpace(Prompt) && string.IsNullOrWhiteSpace(BlastRadius);
}
