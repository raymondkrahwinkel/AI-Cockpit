namespace Cockpit.Plugin.Docker.Engine;

/// <summary>
/// The plugin's own thin seam over the Docker Engine API. Keeping the MCP tools behind this interface (rather than
/// touching <c>Docker.DotNet</c> directly) means the tool layer — the part that carries the consent gate — is
/// testable with a fake daemon, and lets the concrete client be swapped later without touching policy.
/// </summary>
internal interface IDockerEngine
{
    /// <summary>
    /// Daemon version/platform info. Touches the daemon.
    /// </summary>
    Task<DockerDaemonInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists containers (<c>docker ps</c>). <paramref name="all"/> includes stopped ones.
    /// </summary>
    Task<IReadOnlyList<DockerContainer>> ListContainersAsync(bool all, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a stopped container.
    /// </summary>
    Task StartContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a running container.
    /// </summary>
    Task StopContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Restarts a container.
    /// </summary>
    Task RestartContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a container; <paramref name="force"/> removes a running one.
    /// </summary>
    Task RemoveContainerAsync(string id, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a single, non-interactive command in a container (<c>docker exec</c>).
    /// </summary>
    Task<ExecResult> ExecAsync(string id, IReadOnlyList<string> command, CancellationToken cancellationToken);

    /// <summary>
    /// Creates and starts a detached container (<c>docker run -d</c>); returns its id. Throws <see cref="ImageNotFoundException"/> when the image is not available locally and could not be found.
    /// </summary>
    Task<string> RunContainerAsync(RunSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// The last <paramref name="tail"/> lines of a container's logs (<c>docker logs --tail</c>), stdout and stderr separated. A read.
    /// </summary>
    Task<ContainerLogs> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the images available locally (<c>docker images</c>). A read.
    /// </summary>
    Task<IReadOnlyList<DockerImage>> ListImagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pulls an image from its registry (<c>docker pull</c>). A change to local state, but not destructive.
    /// </summary>
    Task PullImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>
    /// Inspects a container (<c>docker inspect</c>): its state, exit code, health, env, mounts and networks. A read.
    /// </summary>
    Task<ContainerInspection> InspectContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// A one-shot resource sample for a container (<c>docker stats --no-stream</c>): CPU, memory, network and block IO. A read.
    /// </summary>
    Task<ContainerStats> GetContainerStatsAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// The processes running inside a container (<c>docker top</c>). A read.
    /// </summary>
    Task<ContainerProcesses> TopContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists local volumes (<c>docker volume ls</c>). A read.
    /// </summary>
    Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes a volume (<c>docker volume rm</c>); <paramref name="force"/> removes one still referenced. Destructive.
    /// </summary>
    Task RemoveVolumeAsync(string name, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Lists networks (<c>docker network ls</c>). A read.
    /// </summary>
    Task<IReadOnlyList<DockerNetwork>> ListNetworksAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reclaims disk by pruning stopped containers, dangling images, or unused volumes (<c>docker … prune</c>). Destructive; returns what was removed.
    /// </summary>
    Task<PruneResult> PruneAsync(PruneTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Tags an image under a new reference (<c>docker tag</c>). A change to local state, not destructive.
    /// </summary>
    Task TagImageAsync(string source, string target, CancellationToken cancellationToken);
}

// What a `prune` should sweep.
internal enum PruneTarget
{
    // Stopped containers.
    Containers,

    // Dangling (untagged) images.
    Images,

    // Volumes not used by any container.
    Volumes,
}

// The engine's local image is missing and could not be resolved — distinct from a daemon-unreachable error so the tool can point the operator at `pull_image` rather than at the endpoint.
internal sealed class ImageNotFoundException(string image) : Exception($"The image '{image}' was not found.")
{
    public string Image { get; } = image;
}

// Engine-agnostic daemon summary.
internal sealed record DockerDaemonInfo(string ServerVersion, string ApiVersion, string Os, string Arch);

// Engine-agnostic container summary — only the fields the MCP surface returns.
internal sealed record DockerContainer(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    IReadOnlyList<DockerPortMapping> Ports);

// A single published/exposed port on a container.
internal sealed record DockerPortMapping(string Type, int PrivatePort, int PublicPort, string? Ip);

// The result of an exec: exit code plus captured output.
internal sealed record ExecResult(long ExitCode, string Stdout, string Stderr);

// A container's captured logs, stdout and stderr separated (a tty container puts everything on stdout).
internal sealed record ContainerLogs(string Stdout, string Stderr);

// Engine-agnostic image summary — only the fields the MCP surface returns.
internal sealed record DockerImage(string Id, IReadOnlyList<string> Tags, long SizeBytes);

// The fields of a container inspect the MCP surface returns — enough to debug why a container is unhealthy or how it is wired.
internal sealed record ContainerInspection(
    string Id,
    string Name,
    string Image,
    string State,
    long ExitCode,
    string? Health,
    IReadOnlyList<string> Env,
    IReadOnlyList<ContainerMount> Mounts,
    IReadOnlyList<ContainerNetwork> Networks);

// A bind/volume mount on a container.
internal sealed record ContainerMount(string Type, string Source, string Destination, bool ReadWrite);

// A container's attachment to a network.
internal sealed record ContainerNetwork(string Name, string IpAddress);

// A one-shot resource sample for a container.
internal sealed record ContainerStats(
    double CpuPercent,
    long MemoryUsageBytes,
    long MemoryLimitBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long BlockReadBytes,
    long BlockWriteBytes);

// The processes inside a container: the column titles and one row of values per process.
internal sealed record ContainerProcesses(IReadOnlyList<string> Titles, IReadOnlyList<IReadOnlyList<string>> Processes);

// A local volume.
internal sealed record DockerVolume(string Name, string Driver, string Mountpoint);

// A network.
internal sealed record DockerNetwork(string Id, string Name, string Driver, string Scope);

// What a prune removed.
internal sealed record PruneResult(long SpaceReclaimedBytes, IReadOnlyList<string> Deleted);

// A structured `docker run -d` request. The MCP tool reconstructs a verbatim command line from this for the
// consent prompt, so dangerous bits (`--privileged`, a bind like `-v /:/host`) are shown literally.
internal sealed record RunSpec(
    string Image,
    string? Name = null,
    IReadOnlyList<string>? Command = null,
    IReadOnlyList<string>? Env = null,
    IReadOnlyList<string>? Publish = null,
    IReadOnlyList<string>? Binds = null,
    bool Privileged = false);
