using System.Diagnostics;
using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Clones;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Worktrees;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1472 (F5.6b2): the server's projects. A project starts as a clone the server makes with its own git credentials
// (in the image those of `agent`, through its git wrapper) under its clone root; no path crosses. The clone runs after
// the answer, and its outcome reaches admin keys on the stream as a `project` event. Writes go through IProjectEditor.
internal static partial class ProjectsEndpoints
{
    public const string EventKind = "project";

    public const string CredentialsRefused = "A repository URL cannot carry a user name, password or token. Use the server's own git credentials.";

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IProjectEditor editor() => services.GetRequiredService<IProjectEditor>();

        // An operate key gets the id and name of the projects in its scope, and nothing that counts the others.
        api.MapGet("/projects", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var pairing = services.GetRequiredService<INodePairingBroker>();
            var known = await services.GetRequiredService<IAssistantReadGateway>().ListProjectsAsync().ConfigureAwait(false);
            await _AuditAsync(services, caller, "api:list_projects", "called", null, cancellationToken).ConfigureAwait(false);
            return caller.Capability == ConnectKeyCapability.Admin
                ? Results.Json(new { projects = known.Select(project => new { id = project.Id, name = project.Name, description = project.Description, path = project.SourceDirectory, defaultProfile = project.DefaultProfileLabel }) })
                : Results.Json(new { projects = known.Where(project => caller.AllowsProject(project.Id, pairing)).Select(project => new { id = project.Id, name = project.Name }) });
        }).RequireOperate();

        api.MapPost("/projects", async (CloneProjectBody body, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            if (string.IsNullOrWhiteSpace(body.RepoUrl))
            {
                return await _RefuseAsync(services, caller, "api:clone_project", "repoUrl is required.").ConfigureAwait(false);
            }

            // Before anything parses it, so no message built from it can carry the secret.
            if (GitCloneUrl.CarriesCredentials(body.RepoUrl))
            {
                return await _RefuseAsync(services, caller, "api:clone_project", CredentialsRefused).ConfigureAwait(false);
            }

            GitCloneUrl parsed;
            try
            {
                parsed = GitCloneUrl.Parse(body.RepoUrl);
            }
            catch (FormatException exception)
            {
                return await _RefuseAsync(services, caller, "api:clone_project", exception.Message).ConfigureAwait(false);
            }

            var branch = body.Branch?.Trim() ?? "";
            if (branch.Length > 0 && !_Branch().IsMatch(branch))
            {
                return await _RefuseAsync(services, caller, "api:clone_project", "That is not a branch name.").ConfigureAwait(false);
            }

            var name = HealthEndpoints.Clean(body.Name).Trim() is { Length: > 0 } given ? given : parsed.Segments[^1];
            var id = Guid.NewGuid().ToString("n");
            await _AuditAsync(services, caller, "api:clone_project", "called", name, cancellationToken).ConfigureAwait(false);
            _ = _CloneAsync(services, caller, id, name, parsed.RemoteUrl, branch);
            return Results.Json(new { id, name }, statusCode: StatusCodes.Status202Accepted);
        }).RequireAdmin();

        // Name and settings only: a body that names a path has nowhere to put it.
        api.MapPatch("/projects/{id}", async (string id, UpdateProjectBody body, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            if (await editor().FindProjectAsync(id).ConfigureAwait(false) is not { } project)
            {
                await _AuditAsync(services, caller, "api:update_project", "not found", null, cancellationToken).ConfigureAwait(false);
                return _NoProject();
            }

            var name = body.Name is null ? project.Name : HealthEndpoints.Clean(body.Name).Trim();
            if (name.Length == 0)
            {
                return await _RefuseAsync(services, caller, "api:update_project", "A project needs a name.").ConfigureAwait(false);
            }

            var updated = project with
            {
                Name = name,
                Description = body.Description ?? project.Description,
                DefaultProfileLabel = body.DefaultProfile ?? project.DefaultProfileLabel,
            };
            if (await editor().UpdateStoredProjectAsync(updated).ConfigureAwait(false) is null)
            {
                return _NoProject();
            }

            await _AuditAsync(services, caller, "api:update_project", "changed", name, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { id, name });
        }).RequireAdmin();

        // The clone stays on disk, as a local remove leaves its folder: it may hold work.
        api.MapDelete("/projects/{id}", async (string id, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            if (await editor().FindProjectAsync(id).ConfigureAwait(false) is not { } project)
            {
                await _AuditAsync(services, caller, "api:remove_project", "not found", null, cancellationToken).ConfigureAwait(false);
                return _NoProject();
            }

            await editor().RemoveProjectAsync(id).ConfigureAwait(false);
            await _AuditAsync(services, caller, "api:remove_project", "removed", project.Name, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { ok = true, id });
        }).RequireAdmin();
    }

    // Clone, switch to the branch, add the project, then tell the stream. A failure adds nothing and says why; the
    // server's own messages never hold a credential, since a URL carrying one never got this far.
    private static async Task _CloneAsync(IServiceProvider services, NodeCaller caller, string id, string name, string remoteUrl, string branch)
    {
        var started = Stopwatch.GetTimestamp();
        var log = services.GetRequiredService<IBackendEventLog>();
        try
        {
            var clone = await services.GetRequiredService<IRepositoryCloneManager>().CloneAsync(remoteUrl).ConfigureAwait(false);
            if (branch.Length > 0)
            {
                await GitCli.RunCheckedAsync(clone.Path, ["switch", branch], CancellationToken.None, GitEnvironment.NonInteractive).ConfigureAwait(false);
            }

            await services.GetRequiredService<IProjectEditor>().AddNewProjectAsync(new Project(id, name)
            {
                SourceDirectories = [new ProjectRepository(clone.Path)],
                GitUrl = remoteUrl,
            }).ConfigureAwait(false);
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            await _AuditAsync(services, caller, "api:clone_project", "cloned", name, CancellationToken.None).ConfigureAwait(false);
            log.Append(EventKind, null, new { id, name, state = "cloned", path = clone.Path, sizeBytes = _SizeOf(clone.Path), seconds });
        }
        catch (Exception exception)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ProjectsEndpoints)).LogWarning(exception, "Cloning project {Project} failed.", name);
            await _AuditAsync(services, caller, "api:clone_project", "clone failed", name, CancellationToken.None).ConfigureAwait(false);
            log.Append(EventKind, null, new { id, name, state = "failed", error = exception.Message, seconds = Stopwatch.GetElapsedTime(started).TotalSeconds });
        }
    }

    // What the clone takes on disk; null when a folder in it cannot be read, rather than a number that is too low.
    private static long? _SizeOf(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Sum(file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<IResult> _RefuseAsync(IServiceProvider services, NodeCaller caller, string tool, string description)
    {
        await _AuditAsync(services, caller, tool, "refused", null, CancellationToken.None).ConfigureAwait(false);
        return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", description);
    }

    private static async Task _AuditAsync(IServiceProvider services, NodeCaller caller, string tool, string outcome, string? subject, CancellationToken cancellationToken)
    {
        if (services.GetService<NodeAccessAuditLog>() is { } audit)
        {
            await audit.RecordAsync(NodeAccessAuditEntry.By(caller, DateTimeOffset.UtcNow, tool, outcome, subject), cancellationToken).ConfigureAwait(false);
        }
    }

    private static IResult _NoProject() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_project", "There is no project with that id on this server.");

    // Only reached behind the group's door, which has checked the caller is there.
    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A projects route ran without a connect-key caller.");

    // A branch git would take, which can never read as an option.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]{0,199}\z")]
    private static partial Regex _Branch();
}

internal sealed record CloneProjectBody(string? RepoUrl, string? Branch, string? Name);

internal sealed record UpdateProjectBody(string? Name, string? Description, string? DefaultProfile);
