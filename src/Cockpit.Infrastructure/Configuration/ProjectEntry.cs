using System.Text.Json.Serialization;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="Project"/> in the <c>projects</c> section of <c>cockpit.json</c>. Carries the
/// profile as the label the project points at, never the profile itself: the two are separate sections, and a
/// project that embedded a copy would drift the moment that profile is edited.
/// </summary>
internal sealed class ProjectEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? SourceDirectory { get; set; }

    public string? GitUrl { get; set; }

    public string? DefaultProfileLabel { get; set; }

    public bool IsolateInWorktreeByDefault { get; set; }

    /// <summary>Absent for a project that changes nothing about the MCP registry, which is most of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectMcpOverlayEntry? McpOverlay { get; set; }

    public string? MemoryRef { get; set; }

    public static ProjectEntry FromDomain(Project project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Description = project.Description,
        SourceDirectory = project.SourceDirectory,
        GitUrl = project.GitUrl,
        DefaultProfileLabel = project.DefaultProfileLabel,
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault,
        McpOverlay = ProjectMcpOverlayEntry.FromDomain(project.McpOverlay),
        MemoryRef = project.MemoryRef,
    };

    public Project ToDomain() => new(Id, Name)
    {
        Description = Description,
        SourceDirectory = SourceDirectory,
        GitUrl = GitUrl,
        DefaultProfileLabel = DefaultProfileLabel,
        IsolateInWorktreeByDefault = IsolateInWorktreeByDefault,
        McpOverlay = McpOverlay?.ToDomain() ?? ProjectMcpOverlay.None,
        MemoryRef = MemoryRef,
    };
}
