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

    public static ProjectJobEntry FromDomain(ProjectJob job) => new()
    {
        Prompt = job.Prompt,
        BlastRadius = job.BlastRadius,
    };

    public ProjectJob ToDomain() => new(Prompt ?? string.Empty, BlastRadius ?? string.Empty);
}
