namespace Cockpit.Core.Projects;

// AC-1013: One place a project's memory can live other than a folder (AC-165/166). Core cannot reference a
// plugin's own registration record, so the host maps it to this plain shape first, same as other plugin
// contributions. Scheme: the MemoryRef prefix (e.g. `depot`). Title/Instruction: how it's named/reached.
public sealed record ProjectMemorySource(string Scheme, string Title, string Instruction);
