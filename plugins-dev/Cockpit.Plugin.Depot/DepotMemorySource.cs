using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot;

/// <summary>
/// The one thing this plugin contributes: Depot as a place a project's memory can live (AC-165/166), so the
/// project editor can offer it beside "Folder" and a session started on such a project is told how to reach it,
/// not only where it is.
/// <para>
/// A separate, host-free type — like <see cref="Cockpit.Plugin.YouTrack.YouTrackProjectField"/> is for a project
/// field — so a test can build and assert on the registration without standing up an <c>ICockpitHost</c>.
/// </para>
/// </summary>
internal static class DepotMemorySource
{
    /// <summary>
    /// The prefix a project's <c>MemoryRef</c> carries this source under — <c>depot:cockpit</c>. Never change it:
    /// an already-linked project's stored reference is matched against it case-insensitively.
    /// </summary>
    public const string Scheme = "depot";

    /// <summary>
    /// What <see cref="DepotPlugin.Initialize"/> hands to <c>ICockpitHost.AddProjectMemorySource</c>. Fixed rather
    /// than built from settings — there is nothing here for an operator to configure, only a scheme to register.
    /// </summary>
    public static ProjectMemorySourceRegistration Registration { get; } = new(
        Scheme,
        "Depot project",
        "Read and write it through the Depot MCP: look the project up by that slug before you start, and write "
            + "back what you learn as you go. If the Depot MCP is not available in this session, say so rather "
            + "than working from memory you cannot see.");
}
