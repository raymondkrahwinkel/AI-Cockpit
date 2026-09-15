namespace Cockpit.Core.Projects;

// AC-491: one piece of work a project offers to start — a saved prompt, and a line saying out loud what it
// changes, writes or sends. A job is not a second kind of session, only the text that would otherwise have to be
// typed into an empty box. AC-493: `Recurrence` absent is a job that does not come round, unchanged in every way.
public sealed record ProjectJob(string Prompt, string BlastRadius, JobRecurrence? Recurrence = null)
{
    // AC-490: what a run is recorded against, so "this job has run" survives the operator rewording the prompt. The
    // row is the identity and the text a property of it: editing keeps the id, deleting and re-adding makes a new job.
    public string Id { get; init; } = NewId();

    public static string NewId() => Guid.NewGuid().ToString("N");

    // Whether this row says nothing yet — an untouched row the editor added and the operator left alone. Dropped
    // on save rather than kept, the same as `ProjectInfoField.IsBlank`.
    public bool IsBlank => string.IsNullOrWhiteSpace(Prompt) && string.IsNullOrWhiteSpace(BlastRadius);
}
