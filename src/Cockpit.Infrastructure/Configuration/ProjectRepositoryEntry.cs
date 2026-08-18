using System.Text.Json.Serialization;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `ProjectRepository` inside a `ProjectEntry.SourceDirectories` (AC-938).
internal sealed class ProjectRepositoryEntry
{
    public string Path { get; set; } = string.Empty;

    // Absent for a repository the operator never labelled — most of them, for the single-repository projects
    // that predate this field entirely.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    public static ProjectRepositoryEntry FromDomain(ProjectRepository repository) => new()
    {
        Path = repository.Path,
        Label = repository.Label,
    };

    public ProjectRepository ToDomain() => new(Path) { Label = Label };
}
