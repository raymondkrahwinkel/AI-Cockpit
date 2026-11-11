using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Services;

/// <summary>
/// The dialogs the Projects page opens (AC-2281): the desktop's dialog service, or the remote window's over its server.
/// </summary>
public interface IProjectDialogs : ICommonDialogs
{
    /// <summary>
    /// Shows the project editor (AC-160) for <paramref name="project"/>, or a new one when null; returns what was
    /// saved, or null if cancelled (persisting is the caller's). The remote window's editor stores on its server while it
    /// waits and so always answers null (AC-2281). The source under <paramref name="sharedSourceKey"/> (AC-247),
    /// when set, reads a fresh <see cref="SharedProjectBinding"/> first so Save writes a claimed field back correctly.
    /// </summary>
    Task<Project?> ShowProjectDialogAsync(Project? project, string? sharedSourceKey = null);

    /// <summary>
    /// Shows the "Finish setting up…" bind step (AC-246) for <paramref name="sharedProjectId"/>, not yet bound on this
    /// machine. Reads the full definition through the source under <paramref name="sourceKey"/> first; returns null both
    /// on cancel and on a failed read (shown as an error either way). Persisting the result is the caller's.
    /// </summary>
    Task<SharedProjectBindResult?> ShowSharedProjectBindingDialogAsync(string sharedProjectId, string sourceName, string sourceKey);

    /// <summary>
    /// Shows AC-620's confirmation screen for publishing <paramref name="project"/> — a local project not yet bound
    /// — to one of the sources under <paramref name="publishSourceKeys"/> (registered sources with
    /// <see cref="ISharedProjectSource.CanPublish"/>). Returns <paramref name="project"/> with its new binding row on
    /// success, or null when cancelled.
    /// </summary>
    Task<Project?> ShowShareProjectDialogAsync(Project project, IReadOnlyList<string> publishSourceKeys);
}
