using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Composition;

// AC-1441: the quick note's memory writes, passed straight through to the in-proc note writer.
internal sealed class ProjectMemoryNotes(IProjectMemoryNoteWriter writer) : IProjectMemoryNotes, ISingletonService
{
    public bool CanAppend(Project project) => writer.CanAppend(project);

    public Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken) =>
        writer.AppendAsync(project, note, cancellationToken);
}
