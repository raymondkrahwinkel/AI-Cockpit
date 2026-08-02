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

    /// <summary>Absent for a project that carries no category (AC-618) — most projects, still, on any given machine.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    public string? SourceDirectory { get; set; }

    public string? GitUrl { get; set; }

    public string? DefaultProfileLabel { get; set; }

    public string? BehaviorPrompt { get; set; }

    public bool IsolateInWorktreeByDefault { get; set; }

    /// <summary>Absent for a project that changes nothing about the MCP registry, which is most of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectMcpOverlayEntry? McpOverlay { get; set; }

    /// <summary>
    /// Legacy on-disk shape (pre-AC-483): the project's one and only memory location, before a project could keep
    /// more than one resource. Still read: <see cref="ToDomain"/> falls back to it when <see cref="Resources"/> is
    /// absent, which is exactly the shape of an old <c>cockpit.json</c> nobody has re-saved since this field
    /// existed. That is the whole migration: load the old field into one <see cref="ProjectResourceRole.Memory"/>
    /// row, and <see cref="Resources"/> takes over from the very next save.
    /// <para>
    /// Also still written by <see cref="FromDomain"/> — deliberately, and only for now (AC-485 is where this field
    /// and this mirroring both go). A build from before <see cref="Resources"/> existed does not know that property
    /// at all: it would read no memory back from a project this build saved, and — because
    /// <see cref="System.Text.Json"/> drops properties it does not recognise — its very first save would erase the
    /// <c>Resources</c> key outright, along with every row an operator added on this build. <c>PluginFields</c> and
    /// <c>AdditionalInfo</c> do not need this same insurance: no build in the field ever knew those keys under a
    /// different name the way this one is the direct predecessor of <see cref="Resources"/>, so there is nothing
    /// asymmetric for them to lose on a rollback.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemoryRef { get; set; }

    /// <summary>Absent for a project that keeps no resource of its own, which is most of them (see <see cref="AdditionalInfo"/> for the same idiom).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectResourceEntry>? Resources { get; set; }

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
        Category = project.Category,
        SourceDirectory = project.SourceDirectory,
        GitUrl = project.GitUrl,
        DefaultProfileLabel = project.DefaultProfileLabel,
        BehaviorPrompt = project.BehaviorPrompt,
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault,
        McpOverlay = ProjectMcpOverlayEntry.FromDomain(project.McpOverlay),
        // Mirrored, not retired yet: project.MemoryRef already only ever reflects the first row of Resources below,
        // so this writes the same value twice under two names. Temporary insurance against a rollback to a build
        // that predates Resources — see the doc comment on MemoryRef above for why that asymmetry is worth guarding
        // and AC-485 for when this mirroring (and the field itself) goes away.
        MemoryRef = project.MemoryRef,
        LogoPath = project.LogoPath,
        LastOpenedAt = project.LastOpenedAt,
        AdditionalInfo = project.AdditionalInfo.Count == 0
            ? null
            : [.. project.AdditionalInfo.Select(ProjectInfoFieldEntry.FromDomain)],
        Resources = project.Resources.Count == 0
            ? null
            : [.. project.Resources.Select(ProjectResourceEntry.FromDomain)],
        PluginFields = project.PluginFields.Count == 0
            ? null
            : project.PluginFields.ToDictionary(link => link.Key, link => link.Value, StringComparer.Ordinal),
    };

    public Project ToDomain() => new(Id, Name)
    {
        Description = Description,
        Category = Category,
        SourceDirectory = SourceDirectory,
        GitUrl = GitUrl,
        DefaultProfileLabel = DefaultProfileLabel,
        BehaviorPrompt = BehaviorPrompt,
        IsolateInWorktreeByDefault = IsolateInWorktreeByDefault,
        McpOverlay = McpOverlay?.ToDomain() ?? ProjectMcpOverlay.None,
        LogoPath = LogoPath,
        LastOpenedAt = LastOpenedAt,
        AdditionalInfo = AdditionalInfo is null ? [] : [.. AdditionalInfo.Select(entry => entry.ToDomain())],
        // The migration (AC-483): Resources is how every project saved from here on carries its memory, but a
        // cockpit.json nobody has re-saved since before this feature still only has the old flat MemoryRef field —
        // read as one Memory row so nothing about what a session is told changes just because the file predates the
        // list. A file with both — hand-edited, written by a build in between, or (now) every file this build
        // itself saves, since FromDomain mirrors the same value into MemoryRef for rollback safety — trusts
        // Resources: it is the fuller, more current answer, and the two are not expected to disagree except in
        // exactly the stale-legacy-value case this precedence exists to resolve. Present-but-empty is not the same
        // as absent: an explicit "Resources": [] means a newer build already saved this project with no resources,
        // and must not fall back to a MemoryRef instead.
        Resources = Resources is not null
            ? [.. Resources.Select(entry => entry.ToDomain())]
            : !string.IsNullOrWhiteSpace(MemoryRef)
                ? [new ProjectResource(MemoryRef, ProjectResourceRole.Memory)]
                : [],
        // Copied rather than handed over: this entry's own property stays settable, and a project is a record whose
        // links nothing is supposed to be able to change behind its back.
        PluginFields = PluginFields is null
            ? ReadOnlyDictionary<string, string>.Empty
            : new Dictionary<string, string>(PluginFields, StringComparer.Ordinal),
    };
}
