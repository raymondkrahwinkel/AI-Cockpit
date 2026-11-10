using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Projects;
using Cockpit.Core.Worktrees;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2285: a project's managed worktrees, named by branch. No answer carries a server path; the files inside one go
// through the file routes with `?worktree=<branch>`.
internal static class WorktreesEndpoints
{
    private const int MaxLabelLength = 64;

    // A worktree made here belongs to no session; the prefix tells it apart from one a session or agent made.
    private const string ApiOwnerPrefix = "api-";
    private const int MaxApiWorktreesPerProject = 10;

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapGet("/projects/{id}/worktrees", (Func<HttpContext, Task<IResult>>)(context => _ListAsync(context, services))).RequireOperate();
        api.MapPost("/projects/{id}/worktrees", (Func<HttpContext, Task<IResult>>)(context => _CreateAsync(context, services)))
            .RequireOperate().Audited("create_worktree", services);
        api.MapDelete("/projects/{id}/worktrees", (Func<HttpContext, Task<IResult>>)(context => _RemoveAsync(context, services)))
            .RequireOperate().Audited("remove_worktree", services);
    }

    // Only a worktree of one of the project's own repositories; a branch of another project reads as unknown.
    internal static async Task<WorktreeRecord?> FindAsync(Project project, string branch, IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetService<IWorktreeManager>() is { } manager
            ? (await manager.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(record => record.Branch == branch && _BelongsTo(record, project))
            : null;

    private static async Task<IResult> _ListAsync(HttpContext context, IServiceProvider services)
    {
        if (await FilesEndpoint.FindProjectAsync(context, services).ConfigureAwait(false) is not { } project ||
            services.GetService<IWorktreeManager>() is not { } manager)
        {
            return Results.NotFound();
        }

        var sessions = services.GetService<ISessionRegistry>();
        var statuses = await manager.GetStatusesAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Json(new
        {
            worktrees = statuses.Where(status => _BelongsTo(status.Record, project)).Select(status => new
            {
                branch = status.Record.Branch,
                baseBranch = status.Record.BaseBranch,
                createdAt = status.Record.CreatedAt,
                exists = status.Exists,
                hasUncommittedChanges = status.HasUncommittedChanges,
                strandableCommits = status.StrandableCommits,
                isOwnerLive = sessions?.Find(status.Record.SessionId) is not null,
            }),
        });
    }

    private static async Task<IResult> _CreateAsync(HttpContext context, IServiceProvider services)
    {
        if (await FilesEndpoint.FindProjectAsync(context, services).ConfigureAwait(false) is not { SourceDirectory: { } source } project ||
            services.GetService<IWorktreeManager>() is not { } manager)
        {
            return Results.NotFound();
        }

        string? label;
        try
        {
            label = (await context.Request.ReadFromJsonAsync<CreateRequest>(context.RequestAborted).ConfigureAwait(false))?.Label;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "Send a JSON body, with an optional label.");
        }

        if (label is { Length: > MaxLabelLength })
        {
            return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", $"label is at most {MaxLabelLength} characters.");
        }

        if ((await manager.ListAsync(context.RequestAborted).ConfigureAwait(false))
            .Count(record => _IsApiMade(record) && _BelongsTo(record, project)) >= MaxApiWorktreesPerProject)
        {
            return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "limit", $"This project already has {MaxApiWorktreesPerProject} worktrees made over the API; remove one first.");
        }

        if (await FilesEndpoint.RefuseIfNoLongerAllowedAsync(context, services).ConfigureAwait(false) is { } revoked)
        {
            return revoked;
        }

        try
        {
            if (await manager.DetectRepositoryAsync(source, context.RequestAborted).ConfigureAwait(false) is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "not_a_repository", "The project is not a git repository with a commit.");
            }

            // Never moves the server's own checkout of the project: an API caller is not the operator at that machine.
            var record = await manager.CreateForSessionAsync(
                ApiOwnerPrefix + Guid.NewGuid().ToString("N"), label, source, WorktreeSourceHandling.LeaveSourceAlone,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { branch = record.Branch }, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "create_failed", "The worktree could not be created.");
        }
    }

    // `force=true` discards uncommitted changes, only in a worktree made here; one a running session uses is never removed.
    private static async Task<IResult> _RemoveAsync(HttpContext context, IServiceProvider services)
    {
        if (await FilesEndpoint.FindProjectAsync(context, services).ConfigureAwait(false) is not { } project ||
            services.GetService<IWorktreeManager>() is not { } manager ||
            context.Request.Query["branch"].ToString() is not { Length: > 0 } branch ||
            await FindAsync(project, branch, services, context.RequestAborted).ConfigureAwait(false) is not { } record)
        {
            return Results.NotFound();
        }

        if (services.GetService<ISessionRegistry>()?.Find(record.SessionId) is not null)
        {
            return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "in_use", "A running session works in this worktree.");
        }

        // Uncommitted work in a worktree a session or agent made is never discarded from here.
        var force = context.Request.Query["force"] == "true";
        if (force && !_IsApiMade(record))
        {
            return BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "force_refused", "Only a worktree made over the API can be removed with force.");
        }

        if (await FilesEndpoint.RefuseIfNoLongerAllowedAsync(context, services).ConfigureAwait(false) is { } revoked)
        {
            return revoked;
        }

        try
        {
            await manager.RemoveAsync(record, force, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { removed = true });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "remove_failed", "The worktree could not be removed; with uncommitted changes, remove it with force.");
        }
    }

    private static bool _IsApiMade(WorktreeRecord record) => record.SessionId.StartsWith(ApiOwnerPrefix, StringComparison.Ordinal);

    private static bool _BelongsTo(WorktreeRecord record, Project project)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.RepositoryRoot));
        return project.SourceDirectories.Any(source =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path)).Equals(repository, comparison));
    }

    private sealed record CreateRequest(string? Label);
}
