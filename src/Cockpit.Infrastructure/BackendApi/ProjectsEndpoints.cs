using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Clones;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
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

    public const string NoRepository = "This Depot project names no repository, so there is nothing to clone on the server.";

    public const string CredentialsRefused = "A repository URL cannot carry a user name, secret or token. Use the server's own git credentials.";

    // AC-1472: off unless set to 1. A file:// clone would copy any folder `agent` can read into /work; a journey sets it.
    public const string AllowFileClonesVariable = "COCKPIT_ALLOW_FILE_CLONES";

    private const string CloneTool = "api:clone_project";

    private const string BindTool = "api:bind_shared_project";

    // _ClaimCloneAsync answers a refusal or a parsed URL, never neither.
    private const string NeitherClaimNorRefusal = "A clone claim came back with neither a URL to clone nor a refusal.";

    // What a clone that is still running, done or failed reported last, by id, for a client that missed its event.
    private static readonly ConcurrentDictionary<string, CloneState> Clones = new(StringComparer.Ordinal);

    // The clone folders a clone is writing now: claimed before it starts and let go when it ends, either way, so a second
    // POST for the same repository meanwhile is refused instead of cloning into the same folder.
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.Ordinal);

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IProjectEditor editor() => services.GetRequiredService<IProjectEditor>();

        // An operate key gets the id and name of the projects in its scope, and nothing that counts the others.
        api.MapGet("/projects", async (CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var pairing = services.GetRequiredService<INodePairingBroker>();
            var known = await services.GetRequiredService<IAssistantReadGateway>().ListProjectsAsync().ConfigureAwait(false);
            var changed = services.GetService<IProjectCatalog>()?.Current.RemoteChangedProjectIds ?? new HashSet<string>();
            await _AuditAsync(services, caller, "api:list_projects", "called", null, cancellationToken).ConfigureAwait(false);
            return caller.Capability == ConnectKeyCapability.Admin
                ? Results.Json(new { projects = known.Select(project => new { id = project.Id, name = project.Name, description = project.Description, path = project.SourceDirectory, defaultProfile = project.DefaultProfileLabel, remoteChanged = changed.Contains(project.Id) }) })
                : Results.Json(new { projects = known.Where(project => caller.AllowsProject(project.Id, pairing)).Select(project => new { id = project.Id, name = project.Name, remoteChanged = changed.Contains(project.Id) }) });
        }).RequireOperate();

        api.MapPost("/projects", async (CloneProjectBody body, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            if (string.IsNullOrWhiteSpace(body.RepoUrl))
            {
                return await _RefuseAsync(services, caller, CloneTool, "repoUrl is required.").ConfigureAwait(false);
            }

            var (refusal, parsed, branch, target) = await _ClaimCloneAsync(services, caller, CloneTool, body.RepoUrl, body.Branch, cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                return refusal ?? throw new UnreachableException(NeitherClaimNorRefusal);
            }

            var name = HealthEndpoints.Clean(body.Name).Trim() is { Length: > 0 } given ? given : parsed.Segments[^1];
            var id = Guid.NewGuid().ToString("n");
            Clones[id] = new CloneState(id, name, "running");
            await _AuditAsync(services, caller, CloneTool, "called", name, CancellationToken.None).ConfigureAwait(false);
            _ = _CloneAsync(services, caller, CloneTool, id, name, parsed.RemoteUrl, branch, target, (editor, path) => editor.AddNewProjectAsync(new Project(id, name)
            {
                SourceDirectories = [new ProjectRepository(path)],
                GitUrl = parsed.RemoteUrl,
            }));
            return Results.Json(new { id, name }, statusCode: StatusCodes.Status202Accepted);
        }).RequireAdmin();

        // AC-1514: what the server's own sources (Depot, as on the desktop) share and it has not added yet, without the
        // bound and hidden ones. A source that failed is one row with its reason, as the Projects page shows it.
        api.MapGet("/projects/shared", async (CancellationToken cancellationToken) =>
        {
            var sources = await services.GetRequiredService<IAssistantReadGateway>().ListSharedProjectsAsync().ConfigureAwait(false);
            await _AuditAsync(services, _Caller(), "api:list_shared_projects", "called", null, cancellationToken).ConfigureAwait(false);
            return Results.Json(new
            {
                sources = sources.Select(source => new
                {
                    name = source.SourceName,
                    error = source.Succeeded ? null : source.Error,
                    projects = source.Projects.Select(project => new { id = project.Id, name = project.Name, description = project.Description, cloneable = project.HasRepository }),
                }),
            });
        }).RequireAdmin();

        // AC-1514: "Add to my projects…" on the server. The definition's repository is cloned through the same checks,
        // claim and clone as POST /projects, then stored bound, so memory, MCP choice and behaviour come with it. Rows
        // only a machine can fill stay empty, as an empty box in the dialog does, and the answer names them.
        api.MapPost("/projects/shared", async (BindSharedProjectBody body, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            var sharedId = body.SharedProjectId?.Trim() ?? "";
            var profileLabel = body.Profile?.Trim() ?? "";
            if (sharedId.Length == 0 || profileLabel.Length == 0)
            {
                return await _RefuseAsync(services, caller, BindTool, "sharedProjectId and profile are required.").ConfigureAwait(false);
            }

            var profile = (await services.GetRequiredService<ISessionProfileStore>().LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(candidate.Label, profileLabel, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return await _RefuseAsync(services, caller, BindTool, "This server has no profile by that name.").ConfigureAwait(false);
            }

            // The id says which connection it came from, as on the desktop (AssistantAgentGateway.BindSharedProjectAsync).
            var source = services.GetRequiredService<ISharedProjectSourceRegistry>().Sources
                .FirstOrDefault(candidate => sharedId.StartsWith(candidate.Key + ":", StringComparison.Ordinal));
            if (source is null)
            {
                await _AuditAsync(services, caller, BindTool, "not found", null, cancellationToken).ConfigureAwait(false);
                return BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_shared_project", "No connection on this server offers a project with that id.");
            }

            var (boundIds, hiddenIds) = SharedProjectSourceLister.VisibilityFilterIds(
                await services.GetRequiredService<IProjectStore>().LoadAsync(cancellationToken).ConfigureAwait(false));
            if (boundIds.Contains(sharedId))
            {
                await _AuditAsync(services, caller, BindTool, "refused", null, cancellationToken).ConfigureAwait(false);
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "already_added", "That project is already added on this server.");
            }

            if (hiddenIds.Contains(sharedId))
            {
                return await _RefuseAsync(services, caller, BindTool, "That project is hidden on this server, so it is not on offer here.").ConfigureAwait(false);
            }

            var (composition, error) = await SharedProjectBindings.ReadAsync(sharedId, source, cancellationToken).ConfigureAwait(false);
            if (composition is null)
            {
                return await _RefuseAsync(services, caller, BindTool, error ?? "Could not read this project's definition.").ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(composition.GitUrl))
            {
                return await _RefuseAsync(services, caller, BindTool, NoRepository).ConfigureAwait(false);
            }

            // Someone else wrote this URL, so it passes every check a typed one does, the credential check first.
            var (refusal, parsed, branch, target) = await _ClaimCloneAsync(services, caller, BindTool, composition.GitUrl, body.Branch, cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                return refusal ?? throw new UnreachableException(NeitherClaimNorRefusal);
            }

            var name = HealthEndpoints.Clean(composition.Name).Trim() is { Length: > 0 } clean ? clean : parsed.Segments[^1];
            var id = Guid.NewGuid().ToString("n");
            Clones[id] = new CloneState(id, name, "running");
            await _AuditAsync(services, caller, BindTool, "called", name, CancellationToken.None).ConfigureAwait(false);
            _ = _CloneAsync(services, caller, BindTool, id, name, parsed.RemoteUrl, branch, target, (editor, path) =>
                editor.AddBoundProjectAsync(
                    composition.ToProject(path, parsed.RemoteUrl, profile.Label, composition.BehaviorPrompt, []) with { Id = id },
                    composition.Checksum));
            return Results.Json(
                new { id, name, unboundResources = composition.MachineRows.Select(row => row.DisplayLabel) },
                statusCode: StatusCodes.Status202Accepted);
        }).RequireAdmin();

        // Read by a client whose stream was down while the clone ended; a server restarted since knows no clone.
        api.MapGet("/projects/clones/{id}", async (string id, CancellationToken cancellationToken) =>
        {
            await _AuditAsync(services, _Caller(), "api:clone_status", "called", null, cancellationToken).ConfigureAwait(false);
            return Clones.TryGetValue(id, out var state)
                ? Results.Json(state)
                : BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_clone", "This server knows no clone with that id.");
        }).RequireAdmin();

        // AC-1570: "Sync now" for a project bound to a Depot source — the same check the desktop's button runs.
        api.MapPost("/projects/{id}/sync", async (string id, CancellationToken cancellationToken) =>
        {
            var caller = _Caller();
            if (await editor().FindProjectAsync(id).ConfigureAwait(false) is null)
            {
                await _AuditAsync(services, caller, "api:sync_project", "not found", null, cancellationToken).ConfigureAwait(false);
                return _NoProject();
            }

            var catalog = services.GetRequiredService<IProjectCatalog>();
            await catalog.SyncNowAsync(id).ConfigureAwait(false);
            await _AuditAsync(services, caller, "api:sync_project", "called", id, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { id, remoteChanged = catalog.Current.RemoteChangedProjectIds.Contains(id) });
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

    // The URL checks and the folder claim every clone passes, whoever wrote the URL: a refusal, or what to clone where.
    private static async Task<(IResult? Refusal, GitCloneUrl? Parsed, string Branch, string Target)> _ClaimCloneAsync(
        IServiceProvider services, NodeCaller caller, string tool, string repoUrl, string? branchText, CancellationToken cancellationToken)
    {
        // Before anything parses it, so no message built from it can carry the secret.
        if (GitCloneUrl.CarriesCredentials(repoUrl))
        {
            return (await _RefuseAsync(services, caller, tool, CredentialsRefused).ConfigureAwait(false), null, "", "");
        }

        if (!_IsAllowedTransport(repoUrl.Trim()))
        {
            return (await _RefuseAsync(services, caller, tool, "The server clones over https or ssh only.").ConfigureAwait(false), null, "", "");
        }

        GitCloneUrl parsed;
        try
        {
            parsed = GitCloneUrl.Parse(repoUrl);
        }
        catch (FormatException exception)
        {
            return (await _RefuseAsync(services, caller, tool, exception.Message).ConfigureAwait(false), null, "", "");
        }

        var branch = branchText?.Trim() ?? "";
        if (branch.Length > 0 && !_Branch().IsMatch(branch))
        {
            return (await _RefuseAsync(services, caller, tool, "That is not a branch name.").ConfigureAwait(false), null, "", "");
        }

        // A folder already there, or one a clone is writing now, is another project's checkout, which a switch to
        // this branch would move under it. The claim is taken first, so two requests at once cannot both pass.
        var clones = services.GetRequiredService<IRepositoryCloneManager>();
        var target = clones.BuildClonePath(await clones.GetEffectiveClonesRootAsync(cancellationToken).ConfigureAwait(false), parsed.RemoteUrl) ?? "";
        if (target.Length == 0 || !InFlight.TryAdd(target, 0) || _ReleaseIfPresent(target))
        {
            await _AuditAsync(services, caller, tool, "refused", null, CancellationToken.None).ConfigureAwait(false);
            return (BackendApiRoutes.Error(StatusCodes.Status409Conflict, "already_cloned", $"{parsed.Slug} is already cloned on this server, or being cloned now."), null, "", "");
        }

        return (null, parsed, branch, target);
    }

    // Clone, switch to the branch, add the project, then tell the stream, which always hears an end: a failure adds
    // nothing and says why. The server's own messages never hold a credential: a URL carrying one never got this far.
    private static async Task _CloneAsync(
        IServiceProvider services, NodeCaller caller, string tool, string id, string name, string remoteUrl, string branch, string target, Func<IProjectEditor, string, Task> addProject)
    {
        // Fire-and-forget (AC-1472): everything that can throw sits inside the try, the logger lookup too, so every
        // end lands as "cloned" or "failed" in Clones and the folder claim is always let go.
        var started = Stopwatch.GetTimestamp();
        ILogger? logger = null;
        CloneState outcome;
        try
        {
            logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ProjectsEndpoints));
            var clone = await services.GetRequiredService<IRepositoryCloneManager>().CloneAsync(remoteUrl, allowedProtocols: _Protocols()).ConfigureAwait(false);
            if (branch.Length > 0)
            {
                await GitCli.RunCheckedAsync(clone.Path, ["switch", branch], CancellationToken.None, GitEnvironment.NonInteractive).ConfigureAwait(false);
            }

            await addProject(services.GetRequiredService<IProjectEditor>(), clone.Path).ConfigureAwait(false);
            outcome = new CloneState(id, name, "cloned", clone.Path, _SizeOf(clone.Path), Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Cloning project {Project} failed.", name);
            outcome = new CloneState(id, name, "failed", Seconds: Stopwatch.GetElapsedTime(started).TotalSeconds, Error: exception.Message);
        }
        finally
        {
            InFlight.TryRemove(target, out _);
        }

        Clones[id] = outcome;
        try
        {
            services.GetRequiredService<IBackendEventLog>().Append(EventKind, null, outcome);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "The end of clone {Project} could not be put on the event stream; its status route has it.", name);
        }

        try
        {
            await _AuditAsync(services, caller, tool, outcome.State == "cloned" ? "cloned" : "clone failed", name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "The end of clone {Project} could not be audited.", name);
        }
    }

    // A folder that exists already lets go of the claim just taken on it, and counts as taken.
    private static bool _ReleaseIfPresent(string target)
    {
        if (!Directory.Exists(target))
        {
            return false;
        }

        InFlight.TryRemove(target, out _);
        return true;
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

internal sealed record BindSharedProjectBody(string? SharedProjectId, string? Profile, string? Branch);

internal sealed record UpdateProjectBody(string? Name, string? Description, string? DefaultProfile);
