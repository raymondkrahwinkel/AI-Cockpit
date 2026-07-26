using System.Collections.ObjectModel;
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

    public string? BehaviorPrompt { get; set; }

    public bool IsolateInWorktreeByDefault { get; set; }

    /// <summary>Absent for a project that changes nothing about the MCP registry, which is most of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectMcpOverlayEntry? McpOverlay { get; set; }

    public string? MemoryRef { get; set; }

    /// <summary>Absent for a project with no logo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogoPath { get; set; }

    /// <summary>Absent for a project no session has ever started on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastOpenedAt { get; set; }

    /// <summary>Absent for a project that keeps no information of its own, which is most of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectInfoFieldEntry>? AdditionalInfo { get; set; }

    /// <summary>
    /// What plugins have this project linked to (AC-317), by their own key. A plain map rather than a typed section:
    /// the host does not know what a key means and must not need to, and a key belonging to a plugin that is not
    /// installed reads and writes back unchanged instead of being dropped on the next save. Absent for an unlinked
    /// project.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? PluginFields { get; set; }

    public static ProjectEntry FromDomain(Project project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Description = project.Description,
        SourceDirectory = project.SourceDirectory,
        GitUrl = project.GitUrl,
        DefaultProfileLabel = project.DefaultProfileLabel,
        BehaviorPrompt = project.BehaviorPrompt,
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault,
        McpOverlay = ProjectMcpOverlayEntry.FromDomain(project.McpOverlay),
        MemoryRef = project.MemoryRef,
        LogoPath = project.LogoPath,
        LastOpenedAt = project.LastOpenedAt,
        AdditionalInfo = project.AdditionalInfo.Count == 0
            ? null
            : [.. project.AdditionalInfo.Select(ProjectInfoFieldEntry.FromDomain)],
        PluginFields = project.PluginFields.Count == 0
            ? null
            : project.PluginFields.ToDictionary(link => link.Key, link => link.Value, StringComparer.Ordinal),
    };

    public Project ToDomain() => new(Id, Name)
    {
        Description = Description,
        SourceDirectory = SourceDirectory,
        GitUrl = GitUrl,
        DefaultProfileLabel = DefaultProfileLabel,
        BehaviorPrompt = BehaviorPrompt,
        IsolateInWorktreeByDefault = IsolateInWorktreeByDefault,
        McpOverlay = McpOverlay?.ToDomain() ?? ProjectMcpOverlay.None,
        MemoryRef = MemoryRef,
        LogoPath = LogoPath,
        LastOpenedAt = LastOpenedAt,
        AdditionalInfo = AdditionalInfo is null ? [] : [.. AdditionalInfo.Select(entry => entry.ToDomain())],
        // Copied rather than handed over: this entry's own property stays settable, and a project is a record whose
        // links nothing is supposed to be able to change behind its back.
        PluginFields = PluginFields is null
            ? ReadOnlyDictionary<string, string>.Empty
            : new Dictionary<string, string>(PluginFields, StringComparer.Ordinal),
    };
}
