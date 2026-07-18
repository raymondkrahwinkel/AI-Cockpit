using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Cockpit.Plugin.Docker.Settings;

namespace Cockpit.Plugin.Docker.Engine;

/// <summary>
/// <see cref="IDockerEngine"/> backed by <c>Docker.DotNet</c>. The client is built lazily from the configured
/// endpoint (blank = the local default socket: npipe on Windows, unix socket elsewhere) and cached; a settings save
/// calls <see cref="Invalidate"/> so the next call rebuilds against the new endpoint.
/// </summary>
internal sealed class DockerEngine(DockerSettings settings) : IDockerEngine, IDisposable
{
    private readonly object _lock = new();
    private DockerClient? _client;

    private IDockerClient _Client()
    {
        lock (_lock)
        {
            if (_client is not null)
            {
                return _client;
            }

            var endpoint = settings.DaemonEndpoint;
            var config = string.IsNullOrWhiteSpace(endpoint)
                ? new DockerClientConfiguration()
                : new DockerClientConfiguration(new Uri(endpoint));
            _client = config.CreateClient();
            return _client;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    public async Task<DockerDaemonInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        var version = await _Client().System.GetVersionAsync(cancellationToken);
        return new DockerDaemonInfo(
            version.Version ?? string.Empty,
            version.APIVersion ?? string.Empty,
            version.Os ?? string.Empty,
            version.Arch ?? string.Empty);
    }

    public async Task<IReadOnlyList<DockerContainer>> ListContainersAsync(bool all, CancellationToken cancellationToken)
    {
        var containers = await _Client().Containers.ListContainersAsync(
            new ContainersListParameters { All = all }, cancellationToken);

        return containers.Select(container => new DockerContainer(
            container.ID ?? string.Empty,
            (container.Names?.FirstOrDefault() ?? string.Empty).TrimStart('/'),
            container.Image ?? string.Empty,
            container.State ?? string.Empty,
            container.Status ?? string.Empty,
            (container.Ports ?? [])
                .Select(port => new DockerPortMapping(
                    port.Type ?? string.Empty,
                    port.PrivatePort,
                    port.PublicPort,
                    string.IsNullOrEmpty(port.IP) ? null : port.IP))
                .ToList()))
            .ToList();
    }

    public Task StartContainerAsync(string id, CancellationToken cancellationToken) =>
        _Client().Containers.StartContainerAsync(id, new ContainerStartParameters(), cancellationToken);

    public Task StopContainerAsync(string id, CancellationToken cancellationToken) =>
        _Client().Containers.StopContainerAsync(id, new ContainerStopParameters(), cancellationToken);

    public Task RestartContainerAsync(string id, CancellationToken cancellationToken) =>
        _Client().Containers.RestartContainerAsync(id, new ContainerRestartParameters(), cancellationToken);

    public Task RemoveContainerAsync(string id, bool force, CancellationToken cancellationToken) =>
        _Client().Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = force }, cancellationToken);

    public async Task<ExecResult> ExecAsync(string id, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var client = _Client();
        var exec = await client.Exec.ExecCreateContainerAsync(id, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = command.ToList(),
        }, cancellationToken);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using (var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, cancellationToken))
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                if (read.EOF)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read.Count);
                if (read.Target == MultiplexedStream.TargetStream.StandardError)
                {
                    stderr.Append(text);
                }
                else
                {
                    stdout.Append(text);
                }
            }
        }

        var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, cancellationToken);
        return new ExecResult(inspect.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<string> RunContainerAsync(RunSpec spec, CancellationToken cancellationToken)
    {
        var (exposed, bindings) = _Ports(spec.Publish);

        var parameters = new CreateContainerParameters
        {
            Image = spec.Image,
            Name = string.IsNullOrWhiteSpace(spec.Name) ? null : spec.Name,
            Cmd = spec.Command?.ToList(),
            Env = spec.Env?.ToList(),
            ExposedPorts = exposed,
            HostConfig = new HostConfig
            {
                Privileged = spec.Privileged,
                Binds = spec.Binds?.ToList(),
                PortBindings = bindings,
            },
        };

        var created = await _Client().Containers.CreateContainerAsync(parameters, cancellationToken);
        await _Client().Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
        return created.ID;
    }

    /// <summary>Parse "host:container[/proto]" (or bare "container[/proto]") publish specs into Docker's exposed-port
    /// and port-binding shapes. A bare container port is exposed but not bound to a host port.</summary>
    private static (Dictionary<string, EmptyStruct> Exposed, Dictionary<string, IList<PortBinding>> Bindings) _Ports(
        IReadOnlyList<string>? publish)
    {
        var exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);
        if (publish is null)
        {
            return (exposed, bindings);
        }

        foreach (var entry in publish)
        {
            var parts = entry.Split(':', 2);
            var containerPart = parts.Length == 2 ? parts[1] : parts[0];
            var hostPart = parts.Length == 2 ? parts[0] : null;

            var portKey = containerPart.Contains('/', StringComparison.Ordinal) ? containerPart : $"{containerPart}/tcp";
            exposed[portKey] = default;

            if (hostPart is { Length: > 0 })
            {
                if (!bindings.TryGetValue(portKey, out var list))
                {
                    list = new List<PortBinding>();
                    bindings[portKey] = list;
                }

                list.Add(new PortBinding { HostPort = hostPart });
            }
        }

        return (exposed, bindings);
    }

    public void Dispose() => Invalidate();
}
