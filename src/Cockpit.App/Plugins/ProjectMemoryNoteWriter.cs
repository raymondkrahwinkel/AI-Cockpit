using System.Globalization;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

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

internal sealed class ProjectMemoryNoteWriter(IProjectMemorySourceRegistry registry) : IProjectMemoryNoteWriter, ISingletonService
{
    public bool CanAppend(Project project) => _Resolve(project) is not null;

    public async Task<ProjectMemoryAppendResult> AppendAsync(Project project, string note, CancellationToken cancellationToken)
    {
        if (_Resolve(project) is not var (source, value))
        {
            return ProjectMemoryAppendResult.Failed("This project's memory is not somewhere a note can be written.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return ProjectMemoryAppendResult.Failed("There is nothing to write.");
        }

        return await source.AppendNoteAsync!(value, Stamp(note, DateTimeOffset.Now), cancellationToken).ConfigureAwait(false);
    }

    // The block a plugin appends verbatim: a blank line so it never glues onto the previous block, then an ISO 8601
    // heading with UTC offset (InvariantCulture) — the file is read outside Cockpit too, and a bare local time is
    // unplaceable half a year later. Depot's `append` adds no separator of its own, hence the leading newline.
    internal static string Stamp(string note, DateTimeOffset at) =>
        $"\n## {at.ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture)}\n\n{note.Trim()}\n";

    // A bare path never parses to a scheme (ProjectMemoryRef.TryParse), so a folder memory resolves to nothing
    // here on purpose: v1 writes through the plugin contract only — see the AC-492 ticket for why not the folder.
    private (ProjectMemorySourceRegistration Source, string Value)? _Resolve(Project project)
    {
        if (!ProjectMemoryRef.TryParse(project.MemoryRef, out var scheme, out var value))
        {
            return null;
        }

        var source = registry.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
        return source?.AppendNoteAsync is null ? null : (source, value);
    }
}
