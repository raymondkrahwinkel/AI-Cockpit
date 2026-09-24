using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The host's own way of putting a note into a project's memory without a session (AC-492): resolves the
/// project's <c>MemoryRef</c> to the plugin source that owns its scheme and hands that source one stamped block.
/// </summary>
public interface IProjectMemoryNoteWriter
{
    /// <summary>
    /// True when <see cref="AppendAsync"/> has somewhere to write for this project — a registered source whose
    /// <see cref="ProjectMemorySourceRegistration.AppendNoteAsync"/> is set. False for no memory, a bare folder
    /// path, an unknown scheme, or a source that only reads. Ask this before offering the project as a destination.
    /// </summary>
    bool CanAppend(Project project);

    /// <summary>
    /// Appends <paramref name="note"/> as one dated block after whatever the memory already holds. Never a throw:
    /// a project <see cref="CanAppend"/> refuses comes back as <see cref="ProjectMemoryAppendOutcome.Failed"/>.
    /// </summary>
    Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken);
}
