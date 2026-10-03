using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Services;

/// <summary>
/// Notes written into a project's memory without a session (AC-492), through the plugin that owns that memory.
/// Local to the desktop: a remote frontend needs it over the line first (AC-1388).
/// </summary>
public interface IProjectMemoryNotes
{
    /// <summary>
    /// Whether a note can land in <paramref name="project"/>'s memory at all.
    /// </summary>
    bool CanAppend(Project project);

    /// <summary>
    /// Appends <paramref name="note"/> to <paramref name="project"/>'s memory.
    /// </summary>
    Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken);
}
