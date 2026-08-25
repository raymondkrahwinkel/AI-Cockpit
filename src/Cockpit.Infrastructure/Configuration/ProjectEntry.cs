using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `Project` in the `projects` section of `cockpit.json`. Carries the
// profile as the label the project points at, never the profile itself: the two are separate sections, and a
// project that embedded a copy would drift the moment that profile is edited.
internal sealed partial class ProjectEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Absent for a project that carries no category (AC-618) — most projects, still, on any given machine.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    // Legacy on-disk shape: the project's one and only repository, before a project could keep more than one.
    // `ToDomain` falls back to it as a single-item SourceDirectories when that list is absent; `FromDomain`
    // still writes it too, mirroring SourceDirectories[0].Path, for the same rollback-safety reason MemoryRef is.
    public string? SourceDirectory { get; set; }

    // Absent for a project with no repository of its own, or exactly one (most projects, still) — see
    // `SourceDirectory` above for why that single-repository shape keeps writing its own legacy field too.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectRepositoryEntry>? SourceDirectories { get; set; }

    public string? GitUrl { get; set; }

    public string? DefaultProfileLabel { get; set; }

    public string? BehaviorPrompt { get; set; }

    // AC-1071: which assistant/persona this project's sessions run as. Absent for a project that leaves it to
    // its profile, which is most of them. Always local — a shared definition never carries it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Assistant { get; set; }

    public bool IsolateInWorktreeByDefault { get; set; }

    // Absent for a project that changes nothing about the MCP registry, which is most of them.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectMcpOverlayEntry? McpOverlay { get; set; }

    // AC-483: legacy single memory location, before multiple resources; `ToDomain` falls back to it when
    // `Resources` is absent, as one Memory row. AC-485: still written by `FromDomain` too, since a
    // pre-`Resources` build would otherwise erase it on its first save (STJ drops unknown properties).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemoryRef { get; set; }

    // Absent for a project that keeps no resource of its own, which is most of them (see `AdditionalInfo` for the same idiom).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectResourceEntry>? Resources { get; set; }

    // Absent for a project with no logo.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogoPath { get; set; }

    // Absent for a project no session has ever started on.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastOpenedAt { get; set; }

    // Absent for a project that keeps no information of its own, which is most of them.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectInfoFieldEntry>? AdditionalInfo { get; set; }

    // AC-317: what plugins have this project linked to, by their own key — a plain map, so a key
    // belonging to an uninstalled plugin reads and writes back unchanged instead of being dropped.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? PluginFields { get; set; }

    // Absent for a project with no cached project password (AC-607) — the name alone routes it through the same
    // encryption and backup scrubbing every other credential in this file already gets (AC-353).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectPassword { get; set; }

    // Absent for a project never published — AC-762's cold-start fallback for the ◆ badge.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SharedSourceName { get; set; }

    public static ProjectEntry FromDomain(Project project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Description = project.Description,
        Category = project.Category,
        SourceDirectory = project.SourceDirectory,
        SourceDirectories = project.SourceDirectories.Count == 0
            ? null
            : [.. project.SourceDirectories.Select(ProjectRepositoryEntry.FromDomain)],
        GitUrl = project.GitUrl,
        DefaultProfileLabel = project.DefaultProfileLabel,
        BehaviorPrompt = project.BehaviorPrompt,
        Assistant = string.IsNullOrWhiteSpace(project.Assistant) ? null : project.Assistant,
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault,
        McpOverlay = ProjectMcpOverlayEntry.FromDomain(project.McpOverlay),
        // AC-485: mirrored, not retired yet — writes the same value twice under two names as temporary
        // insurance against a rollback to a pre-Resources build (see the doc comment on MemoryRef above).
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
        ProjectPassword = project.ProjectPassword,
        SharedSourceName = project.SharedSourceName,
    };

    public Project ToDomain()
    {
        var assistant = _MigratedAssistant(out var behaviorPrompt);

        return new Project(Id, Name)
        {
            Description = Description,
            Category = Category,
            // Migration: an old cockpit.json only has the flat SourceDirectory field — read as its single repository,
            // same fallback ToDomain applies to MemoryRef above. Present-but-empty ([]) is not absent, so it must not
            // fall back to SourceDirectory — that means a newer build already saved this project with no repository.
            SourceDirectories = SourceDirectories is not null
                ? [.. SourceDirectories.Select(entry => entry.ToDomain())]
                : !string.IsNullOrWhiteSpace(SourceDirectory)
                    ? [new ProjectRepository(SourceDirectory)]
                    : [],
            GitUrl = GitUrl,
            DefaultProfileLabel = DefaultProfileLabel,
            BehaviorPrompt = behaviorPrompt,
            Assistant = assistant,
            IsolateInWorktreeByDefault = IsolateInWorktreeByDefault,
            McpOverlay = McpOverlay?.ToDomain() ?? ProjectMcpOverlay.None,
            LogoPath = LogoPath,
            LastOpenedAt = LastOpenedAt,
            AdditionalInfo = AdditionalInfo is null ? [] : [.. AdditionalInfo.Select(entry => entry.ToDomain())],
            // AC-483: a pre-Resources file's flat MemoryRef reads as one Memory row; a file with both trusts
            // Resources as the fuller, current answer. Present-but-empty is not absent: an explicit "Resources":
            // [] means a newer build already saved with none, and must not fall back to MemoryRef instead.
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
            ProjectPassword = ProjectPassword,
            SharedSourceName = SharedSourceName,
        };
    }

    // AC-1071: before this ticket the persona lived in `BehaviorPrompt` ("Gebruik Zyra"), which travels with a
    // shared project and imposed one operator's assistant on everyone who bound it. Read back as the assistant it
    // always was — but only when the whole field is that sentence and nothing else.
    private string? _MigratedAssistant(out string? behaviorPrompt)
    {
        behaviorPrompt = BehaviorPrompt;
        if (!string.IsNullOrWhiteSpace(Assistant) || string.IsNullOrWhiteSpace(BehaviorPrompt))
        {
            return string.IsNullOrWhiteSpace(Assistant) ? null : Assistant;
        }

        // Whole-field only, deliberately: a mixed prompt keeps real project conventions after the persona
        // sentence, and guessing where one ends and the other begins would silently throw those away.
        var match = LegacyAssistantPromptRegex().Match(BehaviorPrompt.Trim());
        if (!match.Success)
        {
            return null;
        }

        behaviorPrompt = null;
        return match.Groups[1].Value;
    }

    // "Gebruik Zyra", "laad Aura", "use Vex" — a verb and a single name, nothing else. Anchored at both ends so
    // a prompt that merely opens with one of these keeps every word of it.
    [GeneratedRegex(@"^(?:gebruik|laad|use|load)\s+(\p{L}[\p{L}\d_-]{1,31})\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyAssistantPromptRegex();
}
