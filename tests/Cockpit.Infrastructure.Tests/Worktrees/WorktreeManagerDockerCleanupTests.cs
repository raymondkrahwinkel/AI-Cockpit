using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// AC-1010: removing a worktree must stop and remove any docker-compose stack it started, and must never touch a
/// container belonging to a *different*, still-live worktree. A fake docker (not a real daemon, which a CI box or a
/// sandboxed session may not have running) proves the link is the hard one the ticket demands — the exact
/// `com.docker.compose.project.working_dir` label docker itself stamps a container with — rather than a guess from
/// a name pattern.
/// </summary>
public sealed class WorktreeManagerDockerCleanupTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-worktree-docker-{Guid.NewGuid():n}");
    private readonly string _repo;
    private readonly WorktreeManager _manager;
    private readonly FakeDockerCli _docker = new();

    public WorktreeManagerDockerCleanupTests()
    {
        _repo = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(_repo);
        _Git(_repo, "init", "-b", "main");
        _Git(_repo, "config", "user.email", "test@example.com");
        _Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "first");

        var configPath = Path.Combine(_tempRoot, "cockpit.json");
        _manager = new WorktreeManager(
            new WorktreeRegistryStore(configPath),
            Path.Combine(_tempRoot, "worktrees"),
            logger: null,
            dockerCli: _docker);
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyTheContainersAndVolumesOfTheWorktreeBeingRemoved_LeavesTheOtherWorktreesStackRunning()
    {
        var alive = await _manager.CreateAsync("session-alive", "alive", _repo);
        var dying = await _manager.CreateAsync("session-dying", "dying", _repo);

        // Two independent dev stacks, each labelled by docker compose with the exact worktree directory it was
        // started from — the same signal RemoveAsync reads.
        _docker.AddStack(workingDir: alive.Path, project: "alive-project", containerId: "container-alive", volumeId: "volume-alive");
        _docker.AddStack(workingDir: dying.Path, project: "dying-project", containerId: "container-dying", volumeId: "volume-dying");

        var notice = await _manager.RemoveAsync(dying);

        Assert.Contains("1 docker container", notice);
        Assert.True(_docker.IsContainerRemoved("container-dying"), "the removed worktree's own container must be gone");
        Assert.True(_docker.IsVolumeRemoved("volume-dying"), "the removed worktree's own volume must be gone");

        Assert.False(_docker.IsContainerRemoved("container-alive"), "a different, still-live worktree's container must never be touched");
        Assert.False(_docker.IsVolumeRemoved("volume-alive"), "a different, still-live worktree's volume must never be touched");
    }

    [Fact]
    public async Task RemoveAsync_NoDockerContainersForThisWorktree_SucceedsWithNoDockerNotice()
    {
        var record = await _manager.CreateAsync("session-clean", "clean", _repo);

        var notice = await _manager.RemoveAsync(record);

        Assert.Null(notice);
    }

    [Fact]
    public async Task RemoveAsync_DockerUnreachable_StillRemovesTheWorktreeAndReportsRatherThanThrowing()
    {
        var record = await _manager.CreateAsync("session-unreachable", "unreachable", _repo);
        _docker.FailWith(new InvalidOperationException("Could not run 'docker' — is it installed and on PATH?"));

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty(await _manager.ListAsync());
        Assert.NotNull(notice);
        Assert.Contains("Could not clean up docker containers", notice);
    }

    private static void _Git(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.RunAsync(workingDirectory, arguments, CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup of the temp fixture.
        }
    }

    // A hand-rolled docker: no real daemon involved, just the exact label-filtering behaviour RemoveAsync depends
    // on, so the test proves the *filtering logic* rather than trusting a real `docker ps` to be there.
    private sealed class FakeDockerCli : IDockerCli
    {
        private readonly List<(string ContainerId, string WorkingDir, string Project, string VolumeId)> _stacks = [];
        private readonly HashSet<string> _removedContainers = [];
        private readonly HashSet<string> _removedVolumes = [];
        private Exception? _failure;

        public void AddStack(string workingDir, string project, string containerId, string volumeId) =>
            _stacks.Add((containerId, workingDir, project, volumeId));

        public void FailWith(Exception exception) => _failure = exception;

        public bool IsContainerRemoved(string containerId) => _removedContainers.Contains(containerId);

        public bool IsVolumeRemoved(string volumeId) => _removedVolumes.Contains(volumeId);

        public Task<DockerCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            if (_failure is { } failure)
            {
                throw failure;
            }

            if (arguments is ["ps", "-a", "--filter", var containerFilter, "--format", _])
            {
                var workingDir = containerFilter["label=com.docker.compose.project.working_dir=".Length..];
                var matches = _stacks.Where(stack => stack.WorkingDir == workingDir && !_removedContainers.Contains(stack.ContainerId));
                var lines = matches.Select(stack => $"{stack.ContainerId}\t{stack.Project}");
                return Task.FromResult(new DockerCliResult(0, string.Join('\n', lines), string.Empty));
            }

            if (arguments is ["rm", "-f", ..])
            {
                foreach (var id in arguments.Skip(2))
                {
                    _removedContainers.Add(id);
                }

                return Task.FromResult(new DockerCliResult(0, string.Empty, string.Empty));
            }

            if (arguments is ["volume", "ls", "-q", "--filter", var volumeFilter])
            {
                var project = volumeFilter["label=com.docker.compose.project=".Length..];
                var matches = _stacks.Where(stack => stack.Project == project && !_removedVolumes.Contains(stack.VolumeId));
                return Task.FromResult(new DockerCliResult(0, string.Join('\n', matches.Select(stack => stack.VolumeId)), string.Empty));
            }

            if (arguments is ["volume", "rm", ..])
            {
                foreach (var id in arguments.Skip(2))
                {
                    _removedVolumes.Add(id);
                }

                return Task.FromResult(new DockerCliResult(0, string.Empty, string.Empty));
            }

            throw new InvalidOperationException($"FakeDockerCli does not understand: docker {string.Join(' ', arguments)}");
        }
    }
}
