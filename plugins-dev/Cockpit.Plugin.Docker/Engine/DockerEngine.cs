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

        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

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

                // Accumulate raw bytes and decode once at EOF — a multi-byte UTF-8 char can straddle a read boundary,
                // and decoding each chunk on its own would turn it into replacement characters.
                var target = read.Target == MultiplexedStream.TargetStream.StandardError ? stderr : stdout;
                target.Write(buffer, 0, read.Count);
            }
        }

        var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, cancellationToken);
        return new ExecResult(
            inspect.ExitCode,
            Encoding.UTF8.GetString(stdout.ToArray()),
            Encoding.UTF8.GetString(stderr.ToArray()));
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

        var client = _Client();
        CreateContainerResponse created;
        try
        {
            created = await client.Containers.CreateContainerAsync(parameters, cancellationToken);
        }
        catch (DockerImageNotFoundException)
        {
            // Translate the raw daemon error into a plugin one, so the tool can say "pull it first" rather than
            // surfacing a misleading daemon-unreachable message.
            throw new ImageNotFoundException(spec.Image);
        }

        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
        }
        catch (Exception)
        {
            // Start failed after create — best-effort remove so a create+start failure leaves no dead container behind.
            try
            {
                await client.Containers.RemoveContainerAsync(
                    created.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }

            throw;
        }

        return created.ID;
    }

    public async Task<ContainerLogs> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken)
    {
        var client = _Client();

        // A tty container's log stream is not multiplexed; a non-tty one interleaves stdout/stderr frames. Ask the
        // container which it is so the read demuxes correctly instead of splicing frame headers into the text.
        var inspect = await client.Containers.InspectContainerAsync(id, cancellationToken);
        var tty = inspect.Config?.Tty ?? false;

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Tail = tail <= 0 ? "all" : tail.ToString(),
        };

        using var stream = await client.Containers.GetContainerLogsAsync(id, tty, parameters, cancellationToken);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);
        return new ContainerLogs(stdout, stderr);
    }

    public async Task<IReadOnlyList<DockerImage>> ListImagesAsync(CancellationToken cancellationToken)
    {
        var images = await _Client().Images.ListImagesAsync(new ImagesListParameters { All = false }, cancellationToken);

        return images.Select(image => new DockerImage(
            (image.ID ?? string.Empty).Replace("sha256:", string.Empty, StringComparison.Ordinal),
            image.RepoTags?.ToList() ?? [],
            image.Size)).ToList();
    }

    public Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        // "repo:tag", "repo" (→ latest), or "repo@sha256:…" (a digest carries its own tag). A blank progress sink is
        // required by the API; the pull's success or failure is what the tool reports, not its byte-by-byte progress.
        var (fromImage, tag) = _SplitImageRef(image);
        return _Client().Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = fromImage, Tag = tag },
            authConfig: null,
            new Progress<JSONMessage>(),
            cancellationToken);
    }

    public async Task<ContainerInspection> InspectContainerAsync(string id, CancellationToken cancellationToken)
    {
        var c = await _Client().Containers.InspectContainerAsync(id, cancellationToken);
        return new ContainerInspection(
            c.ID ?? string.Empty,
            (c.Name ?? string.Empty).TrimStart('/'),
            c.Config?.Image ?? string.Empty,
            c.State?.Status ?? string.Empty,
            c.State?.ExitCode ?? 0,
            c.State?.Health?.Status,
            c.Config?.Env?.ToList() ?? [],
            (c.Mounts ?? []).Select(mount => new ContainerMount(
                mount.Type ?? string.Empty, mount.Source ?? string.Empty, mount.Destination ?? string.Empty, mount.RW)).ToList(),
            (c.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                .Select(network => new ContainerNetwork(network.Key, network.Value?.IPAddress ?? string.Empty)).ToList());
    }

    public async Task<ContainerStats> GetContainerStatsAsync(string id, CancellationToken cancellationToken)
    {
        // A streamed read stopped after the second sample: the first carries no previous-CPU baseline, so a CPU
        // percentage can only be computed once two samples are in hand (the same two `docker stats` reads itself).
        var samples = new List<ContainerStatsResponse>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new Progress<ContainerStatsResponse>(sample =>
        {
            samples.Add(sample);
            if (samples.Count >= 2)
            {
                stop.Cancel();
            }
        });

        try
        {
            await _Client().Containers.GetContainerStatsAsync(
                id, new ContainerStatsParameters { Stream = true }, progress, stop.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Expected: we cancelled the stream ourselves once the second sample arrived.
        }

        var latest = samples.LastOrDefault() ?? throw new InvalidOperationException("The container reported no stats.");
        var cpuPercent = _CpuPercent(latest);
        var networks = latest.Networks?.Values ?? [];
        var blkRead = _BlockIo(latest, "Read");
        var blkWrite = _BlockIo(latest, "Write");
        return new ContainerStats(
            cpuPercent,
            (long)latest.MemoryStats.Usage,
            (long)latest.MemoryStats.Limit,
            (long)networks.Sum(network => (decimal)network.RxBytes),
            (long)networks.Sum(network => (decimal)network.TxBytes),
            blkRead,
            blkWrite);
    }

    private static double _CpuPercent(ContainerStatsResponse s)
    {
        var cpuDelta = (double)s.CPUStats.CPUUsage.TotalUsage - s.PreCPUStats.CPUUsage.TotalUsage;
        var systemDelta = (double)s.CPUStats.SystemUsage - s.PreCPUStats.SystemUsage;
        if (systemDelta <= 0 || cpuDelta < 0)
        {
            return 0;
        }

        int cpus = s.CPUStats.OnlineCPUs > 0 ? (int)s.CPUStats.OnlineCPUs : (s.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
        return Math.Round(cpuDelta / systemDelta * cpus * 100.0, 2);
    }

    private static long _BlockIo(ContainerStatsResponse s, string op) =>
        (long)(s.BlkioStats?.IoServiceBytesRecursive?
            .Where(entry => string.Equals(entry.Op, op, StringComparison.OrdinalIgnoreCase))
            .Sum(entry => (decimal)entry.Value) ?? 0);

    public async Task<ContainerProcesses> TopContainerAsync(string id, CancellationToken cancellationToken)
    {
        var top = await _Client().Containers.ListProcessesAsync(id, new ContainerListProcessesParameters(), cancellationToken);
        return new ContainerProcesses(
            top.Titles?.ToList() ?? [],
            (top.Processes ?? []).Select(row => (IReadOnlyList<string>)row.ToList()).ToList());
    }

    public async Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(CancellationToken cancellationToken)
    {
        var volumes = await _Client().Volumes.ListAsync(cancellationToken);
        return (volumes.Volumes ?? [])
            .Select(volume => new DockerVolume(volume.Name ?? string.Empty, volume.Driver ?? string.Empty, volume.Mountpoint ?? string.Empty))
            .ToList();
    }

    public Task RemoveVolumeAsync(string name, bool force, CancellationToken cancellationToken) =>
        _Client().Volumes.RemoveAsync(name, force, cancellationToken);

    public async Task<IReadOnlyList<DockerNetwork>> ListNetworksAsync(CancellationToken cancellationToken)
    {
        var networks = await _Client().Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken);
        return networks
            .Select(network => new DockerNetwork(network.ID ?? string.Empty, network.Name ?? string.Empty, network.Driver ?? string.Empty, network.Scope ?? string.Empty))
            .ToList();
    }

    public async Task<PruneResult> PruneAsync(PruneTarget target, CancellationToken cancellationToken)
    {
        var client = _Client();
        return target switch
        {
            PruneTarget.Containers => await _Prune(() => client.Containers.PruneContainersAsync(new ContainersPruneParameters(), cancellationToken),
                response => new PruneResult((long)response.SpaceReclaimed, response.ContainersDeleted?.ToList() ?? [])),
            PruneTarget.Images => await _Prune(() => client.Images.PruneImagesAsync(new ImagesPruneParameters(), cancellationToken),
                response => new PruneResult((long)response.SpaceReclaimed, (response.ImagesDeleted ?? []).Select(deleted => deleted.Deleted ?? deleted.Untagged ?? string.Empty).Where(text => text.Length > 0).ToList())),
            _ => await _Prune(() => client.Volumes.PruneAsync(new VolumesPruneParameters(), cancellationToken),
                response => new PruneResult((long)response.SpaceReclaimed, response.VolumesDeleted?.ToList() ?? [])),
        };
    }

    private static async Task<PruneResult> _Prune<T>(Func<Task<T>> prune, Func<T, PruneResult> project) => project(await prune());

    public Task TagImageAsync(string source, string target, CancellationToken cancellationToken)
    {
        var (repository, tag) = _SplitImageRef(target);
        return _Client().Images.TagImageAsync(source, new ImageTagParameters { RepositoryName = repository, Tag = tag }, cancellationToken);
    }

    private static (string FromImage, string? Tag) _SplitImageRef(string image)
    {
        if (image.Contains('@', StringComparison.Ordinal))
        {
            return (image, null);
        }

        // Only a ':' after the last '/' is a tag — a registry host may carry a port ("registry:5000/repo").
        var lastSlash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        return colon > lastSlash
            ? (image[..colon], image[(colon + 1)..])
            : (image, "latest");
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
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            // "container", "host:container", or "ip:host:container" — the container part may carry an optional /proto.
            var parts = entry.Split(':');
            string? hostIp = null;
            string? hostPart;
            string containerPart;
            switch (parts.Length)
            {
                case 1:
                    hostPart = null;
                    containerPart = parts[0];
                    break;
                case 2:
                    hostPart = parts[0];
                    containerPart = parts[1];
                    break;
                case 3:
                    hostIp = parts[0];
                    hostPart = parts[1];
                    containerPart = parts[2];
                    break;
                default:
                    // Unrecognized shape (e.g. bracketed IPv6, a port range) — skip rather than mis-bind it.
                    continue;
            }

            if (string.IsNullOrWhiteSpace(containerPart))
            {
                continue;
            }

            var portKey = containerPart.Contains('/', StringComparison.Ordinal) ? containerPart : $"{containerPart}/tcp";
            exposed[portKey] = default;

            if (!string.IsNullOrWhiteSpace(hostPart))
            {
                if (!bindings.TryGetValue(portKey, out var list))
                {
                    list = new List<PortBinding>();
                    bindings[portKey] = list;
                }

                list.Add(new PortBinding { HostPort = hostPart, HostIP = string.IsNullOrEmpty(hostIp) ? null : hostIp });
            }
        }

        return (exposed, bindings);
    }

    public void Dispose() => Invalidate();
}
