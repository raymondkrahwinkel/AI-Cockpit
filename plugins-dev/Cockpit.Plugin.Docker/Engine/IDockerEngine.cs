namespace Cockpit.Plugin.Docker.Engine;

/// <summary>
/// The plugin's own thin seam over the Docker Engine API. Keeping the MCP tools behind this interface (rather than
/// touching <c>Docker.DotNet</c> directly) means the tool layer — the part that carries the consent gate — is
/// testable with a fake daemon, and lets the concrete client be swapped later without touching policy.
/// </summary>
internal interface IDockerEngine
{
    /// <summary>Daemon version/platform info. Touches the daemon.</summary>
    Task<DockerDaemonInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>Lists containers (<c>docker ps</c>). <paramref name="all"/> includes stopped ones.</summary>
    Task<IReadOnlyList<DockerContainer>> ListContainersAsync(bool all, CancellationToken cancellationToken);

    /// <summary>Starts a stopped container.</summary>
    Task StartContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>Stops a running container.</summary>
    Task StopContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>Restarts a container.</summary>
    Task RestartContainerAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes a container; <paramref name="force"/> removes a running one.</summary>
    Task RemoveContainerAsync(string id, bool force, CancellationToken cancellationToken);

    /// <summary>Runs a single, non-interactive command in a container (<c>docker exec</c>).</summary>
    Task<ExecResult> ExecAsync(string id, IReadOnlyList<string> command, CancellationToken cancellationToken);

    /// <summary>Creates and starts a detached container (<c>docker run -d</c>); returns its id.</summary>
    Task<string> RunContainerAsync(RunSpec spec, CancellationToken cancellationToken);
}

/// <summary>Engine-agnostic daemon summary.</summary>
internal sealed record DockerDaemonInfo(string ServerVersion, string ApiVersion, string Os, string Arch);

/// <summary>Engine-agnostic container summary — only the fields the MCP surface returns.</summary>
internal sealed record DockerContainer(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    IReadOnlyList<DockerPortMapping> Ports);

/// <summary>A single published/exposed port on a container.</summary>
internal sealed record DockerPortMapping(string Type, int PrivatePort, int PublicPort, string? Ip);

/// <summary>The result of an exec: exit code plus captured output.</summary>
internal sealed record ExecResult(long ExitCode, string Stdout, string Stderr);

/// <summary>
/// A structured <c>docker run -d</c> request. The MCP tool reconstructs a verbatim command line from this for the
/// consent prompt, so dangerous bits (<c>--privileged</c>, a bind like <c>-v /:/host</c>) are shown literally.
/// </summary>
internal sealed record RunSpec(
    string Image,
    string? Name = null,
    IReadOnlyList<string>? Command = null,
    IReadOnlyList<string>? Env = null,
    IReadOnlyList<string>? Publish = null,
    IReadOnlyList<string>? Binds = null,
    bool Privileged = false);
