namespace Cockpit.Core.Projects;

// AC-1514: the "Add to my projects…" composition, out of App's binding dialog so the dialog, the assistant's
// bind_shared_project and a server's POST /projects/shared compose a bound project the same way (AC-246, AC-798).
// In Core, not Infrastructure: the dialog's view model may not reach Infrastructure (AC-1441), and Core holds no
// plugin SDK type, so the definition comes in as its fields (Infrastructure's SharedProjectBindings reads one).
/// <summary>
/// A shared definition read for binding: what travels by itself, the rows only this machine can fill in, and the
/// project they make together.
/// </summary>
public sealed class SharedProjectComposition
{
    private readonly string _sharedProjectId;
    private readonly bool _isolateInWorktreeByDefault;
    private readonly IReadOnlyList<string>? _enabledMcpServerNames;
    private readonly byte[]? _logoBytes;

    // Resource rows already portable by shape — copied straight onto the new project, never asked about (AC-605's
    // table: nothing to ask when it already travels).
    private readonly List<ProjectResource> _portableResources = [];

    public SharedProjectComposition(
        string sharedProjectId,
        string sourceName,
        string name,
        string? description,
        string? gitUrl,
        string? behaviorPrompt,
        bool isolateInWorktreeByDefault,
        IReadOnlyList<string>? enabledMcpServerNames,
        byte[]? logoBytes,
        IEnumerable<SharedProjectDefinitionResource> resources)
    {
        _sharedProjectId = sharedProjectId;
        SourceName = sourceName;
        Name = name;
        Description = description;
        GitUrl = gitUrl;
        BehaviorPrompt = behaviorPrompt;
        _isolateInWorktreeByDefault = isolateInWorktreeByDefault;
        _enabledMcpServerNames = enabledMcpServerNames;
        _logoBytes = logoBytes;

        var machineRows = new List<SharedProjectMachineRow>();
        foreach (var resource in resources)
        {
            var role = Enum.TryParse<ProjectResourceRole>(resource.Role, ignoreCase: true, out var parsed)
                ? parsed
                : ProjectResourceRole.Reference; // Unrecognised role: fall back to the least powerful one (looked up, never obeyed or written).

            // ClassifyScope itself answers null for a blank reference (nothing to judge), which would otherwise fall
            // into the "already portable" branch below and silently add a resource row that names nothing at all —
            // exactly the row AC-246 exists to ask about instead.
            var isPlaceholder = string.IsNullOrWhiteSpace(resource.Reference);
            if (isPlaceholder || ProjectResourcePathPortability.ClassifyScope(resource.Reference) == ProjectResourceScope.Machine)
            {
                machineRows.Add(new SharedProjectMachineRow(role, resource.Label, isPlaceholder ? null : resource.Reference));
            }
            else
            {
                // Already portable (Repo/Home/Instance) — travels on its own; nothing to ask.
                _portableResources.Add(new ProjectResource(resource.Reference, role) { Label = resource.Label });
            }
        }

        MachineRows = machineRows;
    }

    /// <summary>
    /// Which connection the definition comes from — "Work", "Personal".
    /// </summary>
    public string SourceName { get; }

    public string Name { get; }

    public string? Description { get; }

    /// <summary>
    /// Where the project can be cloned from; null when the definition names no repository.
    /// </summary>
    public string? GitUrl { get; }

    public string? BehaviorPrompt { get; }

    /// <summary>
    /// Machine-specific rows the definition names but carries no value for, in the definition's order.
    /// </summary>
    public IReadOnlyList<SharedProjectMachineRow> MachineRows { get; }

    /// <summary>
    /// The bound project. Its first resource is the shared definition as memory; <paramref name="machineResources"/>
    /// are the filled-in <see cref="MachineRows"/>, a row left blank simply absent.
    /// </summary>
    // The memory row is prepended rather than appended so it is always what `Project.MemoryRef` resolves to, even if a
    // hand-edited definition somehow also carried its own Memory-role row (AC-246, AC-604, AC-245, AC-242).
    public Project ToProject(
        string? sourceDirectory,
        string? gitUrl,
        string? profileLabel,
        string? behaviorPrompt,
        IEnumerable<ProjectResource> machineResources) =>
        new(Guid.NewGuid().ToString("n"), Name)
        {
            Description = Description,
            // Single-repository only (AC-938 non-goal): a shared definition never carries more than one folder.
            SourceDirectories = string.IsNullOrWhiteSpace(sourceDirectory) ? [] : [new(sourceDirectory.Trim())],
            GitUrl = gitUrl,
            DefaultProfileLabel = profileLabel,
            BehaviorPrompt = string.IsNullOrWhiteSpace(behaviorPrompt) ? null : behaviorPrompt.Trim(),
            // AC-1071: deliberately not taken from the shared definition — the assistant is this operator's own
            // answer, and a bound project that set one would be exactly the imposition that ticket removed.
            Assistant = null,
            IsolateInWorktreeByDefault = _isolateInWorktreeByDefault,
            // The shared logo's bytes (AC-763), already downloaded with the definition; the editor copies the temp
            // file into the cockpit's own logo folder on add.
            LogoPath = TempLogoFile.WriteOrNull(_logoBytes),
            McpOverlay = _enabledMcpServerNames is { } enabled
                ? new ProjectMcpOverlay { EnabledServerNames = enabled }
                : ProjectMcpOverlay.None,
            Resources =
            [
                new ProjectResource(_sharedProjectId, ProjectResourceRole.Memory),
                .. _portableResources,
                .. machineResources,
            ],
            // AC-762: the ◆ badge's fallback for a cold start — see Project.SharedSourceName.
            SharedSourceName = SourceName,
        };
}

/// <summary>
/// One resource row of a shared definition as it was written: a role by name, a reference that may be blank.
/// </summary>
public sealed record SharedProjectDefinitionResource(string Role, string Reference, string? Label);

/// <summary>
/// One machine-specific resource row of a shared definition (AC-246): role and label came from the definition, the
/// reference is this machine's to fill in. <paramref name="OriginalReference"/> is the writer's own, context only.
/// </summary>
public sealed record SharedProjectMachineRow(ProjectResourceRole Role, string? Label, string? OriginalReference)
{
    public string DisplayLabel => Label is { Length: > 0 } ? Label : $"{Role} reference";
}
