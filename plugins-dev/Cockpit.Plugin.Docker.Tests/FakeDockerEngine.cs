using Cockpit.Plugin.Docker.Engine;

namespace Cockpit.Plugin.Docker.Tests;

/// <summary>A fake <see cref="IDockerEngine"/> so the MCP surface is tested without a running daemon. Records the
/// calls it received so tests can assert what reached the engine.</summary>
internal sealed class FakeDockerEngine : IDockerEngine
{
    public DockerDaemonInfo Info { get; set; } = new("27.0.0", "1.47", "linux", "amd64");

    public IReadOnlyList<DockerContainer> Containers { get; set; } = [];

    public ExecResult ExecResultValue { get; set; } = new(0, "ok", string.Empty);

    public string RunReturnsId { get; set; } = "newcontainerid";

    public ContainerLogs LogsValue { get; set; } = new("stdout line", string.Empty);

    public IReadOnlyList<DockerImage> Images { get; set; } = [];

    /// <summary>When set, every call throws it — to exercise the sanitized-error path.</summary>
    public Exception? Throw { get; set; }

    public List<string> Started { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<string> Restarted { get; } = [];
    public List<(string Id, bool Force)> Removed { get; } = [];
    public List<(string Id, IReadOnlyList<string> Command)> Execs { get; } = [];
    public List<RunSpec> Runs { get; } = [];
    public List<(string Id, int Tail)> LogReads { get; } = [];
    public List<string> Pulled { get; } = [];

    public Task<DockerDaemonInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        _Guard();
        return Task.FromResult(Info);
    }

    public Task<IReadOnlyList<DockerContainer>> ListContainersAsync(bool all, CancellationToken cancellationToken)
    {
        _Guard();
        return Task.FromResult(Containers);
    }

    public Task StartContainerAsync(string id, CancellationToken cancellationToken)
    {
        _Guard();
        Started.Add(id);
        return Task.CompletedTask;
    }

    public Task StopContainerAsync(string id, CancellationToken cancellationToken)
    {
        _Guard();
        Stopped.Add(id);
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(string id, CancellationToken cancellationToken)
    {
        _Guard();
        Restarted.Add(id);
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string id, bool force, CancellationToken cancellationToken)
    {
        _Guard();
        Removed.Add((id, force));
        return Task.CompletedTask;
    }

    public Task<ExecResult> ExecAsync(string id, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        _Guard();
        Execs.Add((id, command));
        return Task.FromResult(ExecResultValue);
    }

    public Task<string> RunContainerAsync(RunSpec spec, CancellationToken cancellationToken)
    {
        _Guard();
        Runs.Add(spec);
        return Task.FromResult(RunReturnsId);
    }

    public Task<ContainerLogs> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken)
    {
        _Guard();
        LogReads.Add((id, tail));
        return Task.FromResult(LogsValue);
    }

    public Task<IReadOnlyList<DockerImage>> ListImagesAsync(CancellationToken cancellationToken)
    {
        _Guard();
        return Task.FromResult(Images);
    }

    public Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        _Guard();
        Pulled.Add(image);
        return Task.CompletedTask;
    }

    private void _Guard()
    {
        if (Throw is not null)
        {
            throw Throw;
        }
    }
}
