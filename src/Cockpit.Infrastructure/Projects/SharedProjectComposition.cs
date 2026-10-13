using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1514: the "Add to my projects…" composition, out of App's binding dialog so the dialog, the assistant's
// bind_shared_project and a server's POST /projects/shared all compose a bound project the same way (AC-246, AC-798).
/// <summary>
/// A shared definition read for binding: what travels by itself, the rows only this machine can fill in, and the
/// project they make together.
/// </summary>
public sealed class SharedProjectComposition
{
    private readonly string _sharedProjectId;
    private readonly SharedProjectBinding _binding;

    // Resource rows already portable by shape — copied straight onto the new project, never asked about (AC-605's
    // table: nothing to ask when it already travels).
    private readonly List<ProjectResource> _portableResources = [];

    public SharedProjectComposition(string sharedProjectId, string sourceName, SharedProjectBinding binding)
    {
        _sharedProjectId = sharedProjectId;
        _binding = binding;
        SourceName = sourceName;

        var machineRows = new List<SharedProjectMachineRow>();
        foreach (var resource in binding.Resources)
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

    public string Name => _binding.Name;

    public string? Description => _binding.Description;

    /// <summary>
    /// Where the project can be cloned from; null when the definition names no repository.
    /// </summary>
    public string? GitUrl => _binding.GitUrl;

    public string? BehaviorPrompt => _binding.BehaviorPrompt;

    /// <summary>
    /// Machine-specific rows the definition names but carries no value for, in the definition's order.
    /// </summary>
    public IReadOnlyList<SharedProjectMachineRow> MachineRows { get; }

    /// <summary>
    /// Reads <paramref name="sharedProjectId"/>'s definition through <paramref name="source"/>; null plus the
    /// source's own reason when it cannot be read.
    /// </summary>
    public static async Task<(SharedProjectComposition? Composition, string? Error)> ReadAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        CancellationToken cancellationToken)
    {
        var result = await source.PrepareBindingAsync(sharedProjectId, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Binding is { } binding
            ? (new SharedProjectComposition(sharedProjectId, source.SourceName, binding), null)
            : (null, result.Error is { Length: > 0 } error ? error : "Could not read this project's definition.");
    }

    /// <summary>
    /// The dialog's "Choose…" route with no window, for the assistant: <paramref name="resourceReferences"/> fills
    /// <see cref="MachineRows"/> by position, and anything but one per row is refused with the rows spelled out.
    /// </summary>
    public static async Task<(Project? Project, string? Refusal)> ComposeAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken)
    {
        var (composition, error) = await ReadAsync(sharedProjectId, source, cancellationToken).ConfigureAwait(false);
        if (composition is null)
        {
            // Unreachable, signed out, or the project gone since it was listed — passed on rather than summarised:
            // "could not add it" tells the operator nothing they can act on.
            return (null, error);
        }

        // Positional, not keyed by label, since two rows can share a label. A blank row is silently dropped by the
        // dialog (fine when the operator sees the empty box); here nobody would, so it's refused (AC-246).
        var rows = composition.MachineRows;
        var given = resourceReferences?.Where(reference => !string.IsNullOrWhiteSpace(reference)).ToList() ?? [];
        if (given.Count != rows.Count)
        {
            return (null, rows.Count == 0
                ? "This project names no resources whose location is this machine's own, so there is nothing to pass in resources."
                : $"This project names {rows.Count} resource(s) whose location is this machine's own, and the shared definition does not carry them. "
                    + "Ask the operator for a local path for each, then call again with resources holding one entry per row, in this order: "
                    + string.Join("; ", rows.Select((row, index) => $"{index + 1}. {row.DisplayLabel}"))
                    + ".");
        }

        // The "Choose…" route, not the "Clone…" one: the folder was pointed at rather than cloned, so no GitUrl.
        return (composition.ToProject(
            sourceDirectory,
            gitUrl: null,
            profileLabel,
            composition.BehaviorPrompt,
            rows.Select((row, index) => new ProjectResource(given[index].Trim(), row.Role) { Label = row.Label })), null);
    }

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
            IsolateInWorktreeByDefault = _binding.IsolateInWorktreeByDefault,
            // The shared logo's bytes (AC-763), already downloaded by PrepareBindingAsync; the editor copies the temp
            // file into the cockpit's own logo folder on add.
            LogoPath = TempLogoFile.WriteOrNull(_binding.LogoBytes),
            McpOverlay = _binding.EnabledMcpServerNames is { } enabled
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
/// One machine-specific resource row of a shared definition (AC-246): role and label came from the definition, the
/// reference is this machine's to fill in. <paramref name="OriginalReference"/> is the writer's own, context only.
/// </summary>
public sealed record SharedProjectMachineRow(ProjectResourceRole Role, string? Label, string? OriginalReference)
{
    public string DisplayLabel => Label is { Length: > 0 } ? Label : $"{Role} reference";
}
