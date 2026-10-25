namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One of the cockpit's projects as a plugin sees it (AC-1737): the id to store, and the name to show.
/// </summary>
/// <param name="Id">
/// The project's stable id — the value a session's project is reported as, and the one to keep.
/// </param>
/// <param name="Name">
/// The operator's name for the project; it can change, so look it up again rather than storing it.
/// </param>
public sealed record PluginProjectInfo(string Id, string Name);
