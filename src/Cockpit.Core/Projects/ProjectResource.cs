namespace Cockpit.Core.Projects;

// AC-1013: One place outside SourceDirectory that matters to a project's sessions (AC-483) — memory folder,
// Depot project, tomorrow an instruction file. Reference: free text (folder path or plugin scheme:value,
// same reasoning as the old Project.MemoryRef). Role: required, not inferred from shape — see ProjectResourceRole.
public sealed record ProjectResource(string Reference, ProjectResourceRole Role)
{
    // What the operator calls this row, shown beside it wherever a project is edited. Null when they never named it — the bare reference is shown instead.
    public string? Label { get; init; }

    // AC-1013: Whether a starting session is told about this row (SessionStartDefaults.Resolve, AC-484).
    // Defaults to true — unlike ProjectInfoField.IsSharedWithSessions, since a memory/instruction row exists
    // specifically to be read or obeyed, so the exception (switched off) is what should require the tick, not the norm.
    public bool ReachesSessions { get; init; } = true;

    // AC-1013: Whether this row's contents (not only location) travel with the session (AC-486) — Instructions
    // role only, opt-in default false (Raymond 2026-07-29: "niet inlinen wat gevoelig is"). Always false for
    // non-Instructions rows and likely-secret References (AC-612), enforced here once, not per-reader.
    public bool SendsContent
    {
        get => _sendsContent && Role == ProjectResourceRole.Instructions && !ProjectResourceSecretPathHeuristic.IsLikelySecretPath(Reference);
        init => _sendsContent = value;
    }

    private readonly bool _sendsContent;
}
