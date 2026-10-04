namespace Cockpit.Core.Abstractions.Projects;

// AC-1472 (F5.6b2): the projects of a server, read and written over its backend API. A project there starts as a clone
// the server makes with its own git credentials; no local path crosses, so AddNew, AddBound and MarkOpened refuse.
public interface IServerProjects : IProjectCatalog, IProjectEditor
{
    /// <summary>
    /// Has the server clone <paramref name="repoUrl"/> at <paramref name="branch"/> and add it as <paramref name="name"/>,
    /// and returns once the server reported the clone done or failed. A URL that carries credentials is refused.
    /// </summary>
    Task<ServerProjectClone> CloneAsync(string repoUrl, string branch, string name, CancellationToken cancellationToken = default);
}

// AC-1472: what the server reported for one clone. `Error` is set when it failed, and then nothing was added.
public sealed record ServerProjectClone(string Id, string Name, string? Path, long? SizeBytes, TimeSpan Duration, string? Error);
