using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1472: a server's projects over its backend API. The list is the server's answer for this key: id and name for an
// operate key, with folder and settings for an admin one. A clone's outcome arrives on the stream the server's
// RemoteBackend reads, which hands each `project` event here.
internal sealed class RemoteProjects(BackendApiClient client) : IServerProjects
{
    private const string NoPath = "A project on a server starts as a clone made there; no local folder crosses the connection.";

    // A large repository over a slow line still has to finish; a stream that never reports back must not hang forever.
    private static readonly TimeSpan ClonePatience = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _clones = new(StringComparer.Ordinal);
    private ProjectCatalogSnapshot _current = ProjectCatalogSnapshot.Empty;

    public ProjectCatalogSnapshot Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var projects = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, ProjectCatalogSnapshot.Empty with { Settings = ProjectSettings.Empty with { Projects = projects } });
        Changed?.Invoke();
    }

    // Shared projects and Depot sync belong to this cockpit's own list; a server's has neither.
    public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SyncNowAsync(string projectId) => Task.CompletedTask;

    public async Task<Project?> FindProjectAsync(string projectId) =>
        (await ReadAsync(client, CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(project => project.Id == projectId);

    public Task<Project> AddNewProjectAsync(Project project) => throw new NotSupportedException(NoPath);

    public Task<Project> AddBoundProjectAsync(Project project) => throw new NotSupportedException(NoPath);

    public Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt) =>
        throw new NotSupportedException("A server records its own sessions' starts.");

    public async Task<Project?> UpdateStoredProjectAsync(Project project)
    {
        try
        {
            await client.SendAsync<JsonElement>(
                HttpMethod.Patch,
                $"api/v1/projects/{Uri.EscapeDataString(project.Id)}",
                new { name = project.Name, description = project.Description, defaultProfile = project.DefaultProfileLabel }).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        await LoadAsync().ConfigureAwait(false);
        return project;
    }

    public async Task RemoveProjectAsync(string projectId)
    {
        try
        {
            await client.SendAsync<JsonElement>(HttpMethod.Delete, $"api/v1/projects/{Uri.EscapeDataString(projectId)}", null).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
        }

        await LoadAsync().ConfigureAwait(false);
    }

    public async Task<ServerProjectClone> CloneAsync(string repoUrl, string branch, string name, CancellationToken cancellationToken = default)
    {
        var accepted = await client.SendAsync<JsonElement>(HttpMethod.Post, "api/v1/projects", new { repoUrl, branch, name }, cancellationToken).ConfigureAwait(false);
        var id = accepted.GetProperty("id").GetString() ?? throw new JsonException("The server accepted a clone without an id.");
        JsonElement outcome;
        try
        {
            outcome = await _Waiting(id).Task.WaitAsync(ClonePatience, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _clones.TryRemove(id, out _);
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return new ServerProjectClone(
            id,
            _Text(outcome, "name") ?? name,
            _Text(outcome, "path"),
            outcome.TryGetProperty("sizeBytes", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
            TimeSpan.FromSeconds(outcome.TryGetProperty("seconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number ? seconds.GetDouble() : 0),
            _Text(outcome, "error"));
    }

    // Called on the stream's thread. An outcome that arrives before its clone's answer waits here for it.
    internal void OnEvent(BackendEvent evt)
    {
        if (evt.Data.ValueKind == JsonValueKind.Object && _Text(evt.Data, "id") is { } id)
        {
            _Waiting(id).TrySetResult(evt.Data);
        }
    }

    // A reconnect resumes the stream after the gap, so each clone still waiting asks the server how it stands: done or
    // failed in the gap ends the wait now. A server restarted since has lost the clone, and says so.
    internal async Task CatchUpAsync()
    {
        foreach (var id in _clones.Where(entry => !entry.Value.Task.IsCompleted).Select(entry => entry.Key).ToList())
        {
            try
            {
                var state = await client.GetAsync<JsonElement>($"api/v1/projects/clones/{Uri.EscapeDataString(id)}", CancellationToken.None).ConfigureAwait(false);
                if (_Text(state, "state") is not "running")
                {
                    _Waiting(id).TrySetResult(state);
                }
            }
            catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
            {
                _Waiting(id).TrySetResult(JsonSerializer.SerializeToElement(new { id, state = "failed", error = "The server restarted while it cloned; check its project list." }));
            }
            catch (Exception exception) when (exception is BackendApiException or HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
            }
        }
    }

    internal static async Task<IReadOnlyList<Project>> ReadAsync(BackendApiClient client, CancellationToken cancellationToken)
    {
        var list = await client.GetAsync<RemoteProjectList>("api/v1/projects", cancellationToken).ConfigureAwait(false);
        return
        [
            .. list.Projects.Select(row => new Project(row.Id, row.Name)
            {
                Description = row.Description,
                SourceDirectories = row.Path is { } path ? [new ProjectRepository(path)] : [],
                DefaultProfileLabel = row.DefaultProfile,
            }),
        ];
    }

    private TaskCompletionSource<JsonElement> _Waiting(string id) =>
        _clones.GetOrAdd(id, _ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));

    private static string? _Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed record RemoteProjectList(IReadOnlyList<RemoteProjectRow> Projects);

internal sealed record RemoteProjectRow(string Id, string Name, string? Description = null, string? Path = null, string? DefaultProfile = null);
