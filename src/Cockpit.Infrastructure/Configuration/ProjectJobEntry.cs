using System.Text.Json.Serialization;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// AC-491: on-disk shape of a `ProjectJob`. Both nullable because a hand-edited config can write `null` here and
// the deserializer assigns it — answered at this boundary, as `ProjectInfoFieldEntry` does, rather than by
// every reader of the domain row.
internal sealed class ProjectJobEntry
{
    public string? Prompt { get; set; }

    // What this job changes, writes or sends, in the operator's own words.
    public string? BlastRadius { get; set; }

    // AC-493: absent for a job that does not come round, which is every job written before recurrence existed —
    // and left out of the file rather than written as null, so such a job's config keeps exactly the shape it had.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobRecurrenceEntry? Recurrence { get; set; }

    public static ProjectJobEntry FromDomain(ProjectJob job) => new()
    {
        Prompt = job.Prompt,
        BlastRadius = job.BlastRadius,
        Recurrence = job.Recurrence is null ? null : JobRecurrenceEntry.FromDomain(job.Recurrence),
    };

    public ProjectJob ToDomain() => new(Prompt ?? string.Empty, BlastRadius ?? string.Empty, Recurrence?.ToDomain());
}
