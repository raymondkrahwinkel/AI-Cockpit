namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>A project's Memory row (AC-483), mirrored here since this contract cannot reference Cockpit.Core.</summary>
/// <param name="Reference">A folder path, or <c>&lt;scheme&gt;:&lt;value&gt;</c> naming a registered source.</param>
/// <param name="Label">The operator's own name for the row, or null for the bare reference.</param>
/// <param name="ReachesSessions">Whether a starting session is told about this row; reported, not filtered here.</param>
public sealed record ProjectMemoryRow(string Reference, string? Label, bool ReachesSessions);
