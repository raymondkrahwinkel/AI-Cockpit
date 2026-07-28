namespace Cockpit.Core.Projects;

/// <summary>
/// One place a project's memory can live other than a folder (AC-165/166), as <see cref="Sessions.SessionStartDefaults"/>
/// reads it. Core does not know a plugin's own registration record — that lives in the plugin contract, which Core
/// deliberately does not reference — so the host maps a plugin's registration to this plain shape before handing it
/// down, the same way it turns other plugin contributions into something Core can read without the dependency.
/// </summary>
/// <param name="Scheme">The prefix a project's <see cref="Project.MemoryRef"/> carries this source under — <c>depot</c> in <c>depot:cockpit</c>. Matched case-insensitively.</param>
/// <param name="Title">How the source is named back to the session — "Depot project".</param>
/// <param name="Instruction">How to reach it, appended after the sentence naming where the memory lives.</param>
public sealed record ProjectMemorySource(string Scheme, string Title, string Instruction);
