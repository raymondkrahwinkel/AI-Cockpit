namespace Cockpit.Core.Projects;

// AC-1013: One place a project's memory can live other than a folder (AC-165/166). Core cannot reference a
// plugin's own registration record, so the host maps it to this plain shape first, same as other plugin
// contributions. Scheme: the MemoryRef prefix (e.g. `depot`). Title/Instruction: how it's named/reached.
public sealed record ProjectMemorySource(string Scheme, string Title, string Instruction)
{
    // AC-1992: how a session loads the brain a stored `<scheme>:<value>` names; null when the source has no brains,
    // or (from the call) when the value is not one of this source's own. Pure: it runs while a start is composed.
    public Func<string, string?>? BrainInstruction { get; init; }
}
