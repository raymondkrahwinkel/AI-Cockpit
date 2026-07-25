namespace Cockpit.Core.Abstractions.Projects;

/// <summary>
/// Keeps a project's logo where the cockpit can still find it (AC-162). The operator points at a file or a URL;
/// this takes a copy into the cockpit's own storage and hands back the path the project stores, so the card keeps
/// its picture when the original is moved, renamed, or on a drive that is not plugged in.
/// </summary>
public interface IProjectLogoStore
{
    /// <summary>
    /// Stores the image at <paramref name="source"/> — a local path or an <c>http(s)</c> URL — as
    /// <paramref name="projectId"/>'s logo, replacing any it already had, and returns the stored path.
    /// <see langword="null"/> when there is nothing to store or it could not be read: a logo is decoration, so a
    /// broken source costs the picture and nothing else.
    /// </summary>
    Task<string?> SaveAsync(string projectId, string source, CancellationToken cancellationToken = default);

    /// <summary>Removes the logo <paramref name="projectId"/> had, if any. Never throws for one that is already gone.</summary>
    void Remove(string projectId);

    /// <summary>
    /// Whether <paramref name="path"/> is a copy this store already holds — how a caller tells "the operator left
    /// the logo alone" from "they pointed at something new", without knowing where the copies live.
    /// </summary>
    bool IsStoredCopy(string path);
}
