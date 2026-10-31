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
public sealed record PluginProjectInfo(string Id, string Name)
{
    /// <summary>
    /// The folder the project's sessions start in, on the machine whose project it is (AC-1547): a server's project
    /// names a folder on that server. Null for a project without a source folder, or from a host before 0.153.0.
    /// </summary>
    public string? SourceDirectory { get; init; }
}
