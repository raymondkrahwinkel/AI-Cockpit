using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
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

    // AC-1472: off unless set to 1. A file:// clone would copy any folder `agent` can read into /work; a journey sets it.
    public const string AllowFileClonesVariable = "COCKPIT_ALLOW_FILE_CLONES";

    // What a clone that is still running, done or failed reported last, by id, for a client that missed its event.
    private static readonly ConcurrentDictionary<string, CloneState> Clones = new(StringComparer.Ordinal);

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

            if (!_IsAllowedTransport(body.RepoUrl.Trim()))
            {
                return await _RefuseAsync(services, caller, "api:clone_project", "The server clones over https or ssh only.").ConfigureAwait(false);
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

            // A folder already there is another project's checkout, which a switch to this branch would move under it.
            var clones = services.GetRequiredService<IRepositoryCloneManager>();
            if (clones.BuildClonePath(await clones.GetEffectiveClonesRootAsync(cancellationToken).ConfigureAwait(false), parsed.RemoteUrl) is { } target
                && Directory.Exists(target))
            {
                await _AuditAsync(services, caller, "api:clone_project", "refused", null, cancellationToken).ConfigureAwait(false);
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "already_cloned", $"That repository is already cloned on this server, at {target}.");
            }

            var name = HealthEndpoints.Clean(body.Name).Trim() is { Length: > 0 } given ? given : parsed.Segments[^1];
            var id = Guid.NewGuid().ToString("n");
            Clones[id] = new CloneState(id, name, "running");
            await _AuditAsync(services, caller, "api:clone_project", "called", name, cancellationToken).ConfigureAwait(false);
            _ = _CloneAsync(services, caller, id, name, parsed.RemoteUrl, branch);
            return Results.Json(new { id, name }, statusCode: StatusCodes.Status202Accepted);
        }).RequireAdmin();

        // Read by a client whose stream was down while the clone ended; a server restarted since knows no clone.
        api.MapGet("/projects/clones/{id}", async (string id, CancellationToken cancellationToken) =>
        {
            await _AuditAsync(services, _Caller(), "api:clone_status", "called", null, cancellationToken).ConfigureAwait(false);
            return Clones.TryGetValue(id, out var state)
                ? Results.Json(state)
                : BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_clone", "This server knows no clone with that id.");
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
            var clone = await services.GetRequiredService<IRepositoryCloneManager>().CloneAsync(remoteUrl, allowedProtocols: _Protocols()).ConfigureAwait(false);
            if (branch.Length > 0)
            {
                await GitCli.RunCheckedAsync(clone.Path, ["switch", branch], CancellationToken.None, GitEnvironment.NonInteractive).ConfigureAwait(false);
            }

            await services.GetRequiredService<IProjectEditor>().AddNewProjectAsync(new Project(id, name)
            {
                SourceDirectories = [new ProjectRepository(clone.Path)],
                GitUrl = remoteUrl,
            }).ConfigureAwait(false);
            var cloned = new CloneState(id, name, "cloned", clone.Path, _SizeOf(clone.Path), Stopwatch.GetElapsedTime(started).TotalSeconds);
            Clones[id] = cloned;
            await _AuditAsync(services, caller, "api:clone_project", "cloned", name, CancellationToken.None).ConfigureAwait(false);
            log.Append(EventKind, null, cloned);
        }
        catch (Exception exception)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ProjectsEndpoints)).LogWarning(exception, "Cloning project {Project} failed.", name);
            var failed = new CloneState(id, name, "failed", Seconds: Stopwatch.GetElapsedTime(started).TotalSeconds, Error: exception.Message);
            Clones[id] = failed;
            await _AuditAsync(services, caller, "api:clone_project", "clone failed", name, CancellationToken.None).ConfigureAwait(false);
            log.Append(EventKind, null, failed);
        }
    }

    // https and ssh (also git's scp form), and file:// only where the server allows it. No host or user may begin with a
    // dash, which ssh would read as an option; ext::, fd:: and a bare path are none of these.
    private static bool _IsAllowedTransport(string url)
    {
        if (url.StartsWith('-') || url.Contains("@-", StringComparison.Ordinal) || url.Contains("://-", StringComparison.Ordinal))
        {
            return false;
        }

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        return schemeEnd < 0
            ? GitCloneUrl.IsScpLike(url)
            : url[..schemeEnd].ToLowerInvariant() is "https" or "ssh" or "git+ssh" or "ssh+git" || (url[..schemeEnd].ToLowerInvariant() == "file" && _FileClonesAllowed());
    }

    private static string[] _Protocols() => _FileClonesAllowed() ? ["https", "ssh", "file"] : ["https", "ssh"];

    private static bool _FileClonesAllowed() => Environment.GetEnvironmentVariable(AllowFileClonesVariable) == "1";

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

// AC-1472: one clone as the stream and the status route report it, in the stream's camelCase.
internal sealed record CloneState(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes = null,
    [property: JsonPropertyName("seconds")] double Seconds = 0,
    [property: JsonPropertyName("error")] string? Error = null);

internal sealed record CloneProjectBody(string? RepoUrl, string? Branch, string? Name);

internal sealed record UpdateProjectBody(string? Name, string? Description, string? DefaultProfile);
