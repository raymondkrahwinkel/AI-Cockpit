using System.Text.Json;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2283: project and worktree choices share the server's existing authenticated client; no local paths cross.
public sealed class RemoteProjectFiles : IFileSourceProvider, IRemoteWorktrees
{
    private readonly BackendApiClient _client;
    private readonly IServerProjects _projects;
    private IReadOnlyDictionary<string, IFileSource> _sources = new Dictionary<string, IFileSource>();

    private RemoteProjectFiles(BackendApiClient client, IServerProjects projects)
    {
        _client = client;
        _projects = projects;
    }

    public static RemoteProjectFiles For(IRemoteServer server) => server is RemoteServer remote
        ? new RemoteProjectFiles(remote.OptionsClient, server.Projects)
        : throw new NotSupportedException("This server does not offer project files and worktrees.");

    public IReadOnlyList<FileConnection> Connections { get; private set; } = [];

    public event EventHandler? ConnectionsChanged;

    public IFileSource Open(string connectionId) => _sources.TryGetValue(connectionId, out var source)
        ? source : throw new FileSourceException(FileSourceErrorKind.NotFound, "This project or worktree is no longer available.");

    public IDisposable Register(IPluginBackendChannel channel) => Files.FileSourceChannel.Register(channel, this);

    public async Task<IReadOnlyList<RemoteManagedWorktree>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _projects.LoadAsync(cancellationToken).ConfigureAwait(false);
        var sources = new Dictionary<string, IFileSource>(StringComparer.Ordinal);
        var connections = new List<FileConnection>();
        var worktrees = new List<RemoteManagedWorktree>();
        foreach (var project in _projects.Current.Settings.Projects)
        {
            Add(project.Id, project.Name, null);
            var answer = await _client.GetAsync<JsonElement>(_Route(project.Id), cancellationToken).ConfigureAwait(false);
            foreach (var row in answer.GetProperty("worktrees").EnumerateArray())
            {
                var branch = row.GetProperty("branch").GetString() ?? throw new InvalidDataException("The server returned a worktree without a branch.");
                worktrees.Add(new RemoteManagedWorktree(project.Id, project.Name, branch,
                    row.GetProperty("exists").GetBoolean(), row.GetProperty("hasUncommittedChanges").GetBoolean(),
                    row.GetProperty("strandableCommits").GetInt32(), row.GetProperty("isOwnerLive").GetBoolean()));
                Add(project.Id, $"{project.Name} / {branch}", branch);
            }
        }
        _sources = sources;
        Connections = connections;
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        return worktrees;

        void Add(string projectId, string label, string? branch)
        {
            var id = $"{Uri.EscapeDataString(projectId)}:{Uri.EscapeDataString(branch ?? string.Empty)}";
            sources.Add(id, new RemoteProjectFileSource(_client, projectId, branch));
            connections.Add(new FileConnection(id, label, "server-project", ReadOnly: false) { CanCreateFile = true });
        }
    }

    public IReadOnlyList<(string Id, string Name)> Projects => [.. _projects.Current.Settings.Projects.Select(project => (project.Id, project.Name))];

    public async Task CreateWorktreeAsync(string projectId, string? label, CancellationToken cancellationToken = default) =>
        await _client.SendAsync<JsonElement>(HttpMethod.Post, _Route(projectId), new { label }, cancellationToken).ConfigureAwait(false);

    public Task RemoveWorktreeAsync(RemoteManagedWorktree worktree, bool force, CancellationToken cancellationToken = default) =>
        _client.DeleteAsync($"{_Route(worktree.ProjectId)}?branch={Uri.EscapeDataString(worktree.Branch)}&force={force.ToString().ToLowerInvariant()}", cancellationToken);

    private static string _Route(string projectId) => $"api/v1/projects/{Uri.EscapeDataString(projectId)}/worktrees";
}
