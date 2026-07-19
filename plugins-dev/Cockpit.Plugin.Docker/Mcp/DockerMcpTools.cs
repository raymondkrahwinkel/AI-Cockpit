using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Plugin.Docker.Compose;
using Cockpit.Plugin.Docker.Engine;
using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugin.Docker.StatusBar;

namespace Cockpit.Plugin.Docker.Mcp;

/// <summary>
/// The <c>cockpit-docker</c> MCP tool surface (AC-84). Each <c>[McpServerTool]</c> method routes through the
/// <see cref="DockerAccessGate"/> before touching the daemon and returns <see cref="McpText"/>-shaped JSON. The tools
/// object is constructed with all its dependencies so the surface is testable against fakes without a running host.
///
/// <para>Policy: reads (<c>daemon_info</c>, <c>list_containers</c>, <c>compose_config</c>) need only the one-time
/// daemon-connection consent. Changes (start/stop/restart/remove, compose up/down/build) are always Dangerous and
/// never remembered. <c>exec</c> and <c>run</c> execute arbitrary code, so they sit behind the exec capability
/// (off by default) and show the literal command in the consent.</para>
/// </summary>
internal sealed class DockerMcpTools(
    DockerSettings settings,
    DockerAccessGate gate,
    IDockerEngine engine,
    IComposeCli compose,
    IDockerCli docker,
    RunningContainerRegistry running)
{
    // ---- Reads -------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "daemon_info")]
    [Description("Returns the Docker daemon's version and platform (server version, API version, OS, architecture). This is the first call that touches the daemon, so it asks the operator for consent once; after that, reads are free for the session. Start here to confirm the daemon is reachable.")]
    public async Task<string> DaemonInfo(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("read the Docker daemon version", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var info = await engine.GetInfoAsync(cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                serverVersion = info.ServerVersion,
                apiVersion = info.ApiVersion,
                os = info.Os,
                arch = info.Arch,
                execEnabled = settings.AllowExec,
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "list_containers")]
    [Description("Lists containers (docker ps). By default includes stopped containers too; set all=false for only running ones. Returns each container's id, name, image, state, status and published ports. Touching the daemon asks for consent the first time in a session; after that this read is free.")]
    public async Task<string> ListContainers(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("Include stopped containers too. Default true.")] bool all = true,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"list containers (all={all})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var containers = await engine.ListContainersAsync(all, cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = containers.Count,
                containers = containers.Select(container => new
                {
                    id = container.Id,
                    name = container.Name,
                    image = container.Image,
                    state = container.State,
                    status = container.Status,
                    ports = container.Ports.Select(port => new
                    {
                        type = port.Type,
                        privatePort = port.PrivatePort,
                        publicPort = port.PublicPort,
                        ip = port.Ip,
                    }),
                }),
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "logs")]
    [Description("Returns the recent logs of a container (docker logs --tail), stdout and stderr separated. Set tail to how many lines from the end you want (default 200; 0 means all). Does not follow — it returns what is there now and completes. Touching the daemon asks for consent the first time in a session; after that this read is free.")]
    public async Task<string> Logs(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The container id or name.")] string container,
        [Description("How many lines from the end to return. Default 200; 0 means all.")] int tail = 200,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"read logs of container \"{container}\" (tail={tail})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var logs = await engine.GetContainerLogsAsync(container, tail, cancellationToken);
            return McpText.Ok(new { ok = true, container, stdout = logs.Stdout, stderr = logs.Stderr });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "list_images")]
    [Description("Lists the images available locally (docker images): each image's id, tags and size in bytes. Use this to see whether an image is already present before run_container (a missing image is why a run fails until you pull_image it). Touching the daemon asks for consent the first time in a session; after that this read is free.")]
    public async Task<string> ListImages(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list local images", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var images = await engine.ListImagesAsync(cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = images.Count,
                images = images.Select(image => new { id = image.Id, tags = image.Tags, sizeBytes = image.SizeBytes }),
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "inspect")]
    [Description("Inspects a container (docker inspect): its state and exit code, health, environment variables, mounts and networks — the read you reach for to debug why a container is unhealthy or how it is wired. Touching the daemon asks for consent the first time in a session; after that this read is free.")]
    public async Task<string> Inspect(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The container id or name.")] string container,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"inspect container \"{container}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var c = await engine.InspectContainerAsync(container, cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                id = c.Id,
                name = c.Name,
                image = c.Image,
                state = c.State,
                exitCode = c.ExitCode,
                health = c.Health,
                env = c.Env,
                mounts = c.Mounts.Select(mount => new { type = mount.Type, source = mount.Source, destination = mount.Destination, readWrite = mount.ReadWrite }),
                networks = c.Networks.Select(network => new { name = network.Name, ip = network.IpAddress }),
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "stats")]
    [Description("Returns a one-shot resource sample for a container (docker stats --no-stream): CPU percent, memory usage and limit in bytes, network rx/tx and block read/write. A read behind the one-time daemon-connection consent.")]
    public async Task<string> Stats(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"read stats of container \"{container}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var s = await engine.GetContainerStatsAsync(container, cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                container,
                cpuPercent = s.CpuPercent,
                memoryUsageBytes = s.MemoryUsageBytes,
                memoryLimitBytes = s.MemoryLimitBytes,
                networkRxBytes = s.NetworkRxBytes,
                networkTxBytes = s.NetworkTxBytes,
                blockReadBytes = s.BlockReadBytes,
                blockWriteBytes = s.BlockWriteBytes,
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "top")]
    [Description("Lists the processes running inside a container (docker top): the column titles and a row per process. A read behind the one-time daemon-connection consent.")]
    public async Task<string> Top(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"list processes in container \"{container}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var top = await engine.TopContainerAsync(container, cancellationToken);
            return McpText.Ok(new { ok = true, container, titles = top.Titles, processes = top.Processes });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "list_volumes")]
    [Description("Lists local volumes (docker volume ls): name, driver and mountpoint. A read behind the one-time daemon-connection consent.")]
    public async Task<string> ListVolumes(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list volumes", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var volumes = await engine.ListVolumesAsync(cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = volumes.Count,
                volumes = volumes.Select(volume => new { name = volume.Name, driver = volume.Driver, mountpoint = volume.Mountpoint }),
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "list_networks")]
    [Description("Lists networks (docker network ls): id, name, driver and scope — for inspecting connectivity between containers. A read behind the one-time daemon-connection consent.")]
    public async Task<string> ListNetworks(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list networks", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var networks = await engine.ListNetworksAsync(cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = networks.Count,
                networks = networks.Select(network => new { id = network.Id, name = network.Name, driver = network.Driver, scope = network.Scope }),
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    // ---- Image pull (a change to local state, never destructive) -----------------------------------------------

    [McpServerTool(Name = "pull_image")]
    [Description("Pulls an image from its registry (docker pull), e.g. \"nginx:latest\" or \"ghcr.io/owner/app:1.2\". A bare name without a tag pulls :latest. This is what you run before run_container when the image is not available locally. A change to local state (not destructive), so it asks the operator afresh each time and is never remembered.")]
    public Task<string> PullImage(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The image reference to pull, e.g. \"nginx:latest\" or \"ghcr.io/owner/app:1.2\".")] string image,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"pull image \"{image}\"", session,
            token => engine.PullImageAsync(image, token),
            new { ok = true, pulled = image }, cancellationToken);

    // ---- Container mutations (always Dangerous, never remembered) ----------------------------------------------

    [McpServerTool(Name = "start_container")]
    [Description("Starts a stopped container. This is a change, so it asks the operator afresh each time and is never remembered.")]
    public Task<string> StartContainer(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"start container \"{container}\"", session,
            token => engine.StartContainerAsync(container, token),
            new { ok = true, started = container }, cancellationToken);

    [McpServerTool(Name = "stop_container")]
    [Description("Stops a running container. This is a change, so it asks the operator afresh each time and is never remembered.")]
    public Task<string> StopContainer(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"stop container \"{container}\"", session,
            token => engine.StopContainerAsync(container, token),
            new { ok = true, stopped = container }, cancellationToken);

    [McpServerTool(Name = "restart_container")]
    [Description("Restarts a container. This is a change, so it asks the operator afresh each time and is never remembered.")]
    public Task<string> RestartContainer(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"restart container \"{container}\"", session,
            token => engine.RestartContainerAsync(container, token),
            new { ok = true, restarted = container }, cancellationToken);

    [McpServerTool(Name = "remove_container")]
    [Description("Removes a container. Set force=true to remove one that is still running. This is a change, so it asks the operator afresh each time and is never remembered.")]
    public Task<string> RemoveContainer(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        [Description("Force-remove a running container. Default false.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"remove container \"{container}\" (force={force})", session,
            token => engine.RemoveContainerAsync(container, force, token),
            new { ok = true, removed = container }, cancellationToken);

    // ---- exec / run (arbitrary code — behind the exec capability) ----------------------------------------------

    [McpServerTool(Name = "exec")]
    [Description("Runs a single, non-interactive command inside a running container and returns its stdout, stderr and exit code. exec is off unless the operator turned it on, and always asks afresh with the literal command shown, never remembered. The command runs as \"/bin/sh -c <command>\".")]
    public async Task<string> Exec(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The container id or name.")] string container,
        [Description("The shell command to run, e.g. \"ls -la /app\".")] string command,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Exec, settings.AllowExec,
            $"exec in container \"{container}\": /bin/sh -c {command}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var result = await engine.ExecAsync(container, ["/bin/sh", "-c", command], cancellationToken);
            return McpText.Ok(new { ok = true, exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    [McpServerTool(Name = "run_container")]
    [Description("Creates and starts a new detached container (docker run -d) and returns its id. Because this runs arbitrary code, it is off unless the exec capability is on, and always asks afresh with the literal 'docker run' command line shown — so dangerous flags like --privileged or a bind mount (-v /:/host) are visible before you approve. Never remembered. The started container appears in the status bar with an operator-only Kill.")]
    public async Task<string> RunContainer(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The image to run, e.g. \"nginx:latest\".")] string image,
        [Description("Optional container name.")] string? name = null,
        [Description("Optional command + args to run instead of the image default, e.g. [\"sleep\", \"3600\"].")] string[]? command = null,
        [Description("Optional environment variables as \"KEY=value\".")] string[]? env = null,
        [Description("Optional port publishes as \"hostPort:containerPort[/proto]\", e.g. \"8080:80\".")] string[]? publish = null,
        [Description("Optional bind mounts as \"hostPath:containerPath[:ro]\". Shown verbatim in the consent.")] string[]? volumes = null,
        [Description("Run privileged. Dangerous — shown verbatim in the consent. Default false.")] bool privileged = false,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec(image, name, command, env, publish, volumes, privileged);

        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Exec, settings.AllowExec, _RunCommandLine(spec), session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var id = await engine.RunContainerAsync(spec, cancellationToken);
            running.Track(id, name ?? string.Empty, image, string.Join(", ", publish ?? []), session);
            return McpText.Ok(new { ok = true, id, image });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (ImageNotFoundException)
        {
            // The real cause is a missing local image, not the daemon — say so, and point at the pull that fixes it,
            // instead of the generic "daemon could not be reached" that sent operators looking at the endpoint.
            return McpText.Error($"The image \"{image}\" is not available locally and could not be found. Pull it first with pull_image, then run_container again.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    // ---- Image tag / push -------------------------------------------------------------------------------------

    [McpServerTool(Name = "tag")]
    [Description("Tags an image under a new reference (docker tag), e.g. source \"myapp:latest\" as \"registry.example.com/myapp:1.2\". A change to local state (not destructive), so it asks the operator afresh each time and is never remembered.")]
    public Task<string> Tag(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The existing image id or reference to tag, e.g. \"myapp:latest\".")] string source,
        [Description("The new reference to give it, e.g. \"registry.example.com/myapp:1.2\".")] string target,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"tag image \"{source}\" as \"{target}\"", session,
            token => engine.TagImageAsync(source, target, token),
            new { ok = true, tagged = target }, cancellationToken);

    [McpServerTool(Name = "push")]
    [Description("Pushes an image to its registry (docker push), e.g. \"registry.example.com/myapp:1.2\". This publishes it outside your machine, so it always asks the operator afresh with the reference shown, and is never remembered. Runs through the docker CLI (registry auth).")]
    public async Task<string> Push(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The image reference to push, e.g. \"registry.example.com/myapp:1.2\".")] string image,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeMutationAsync($"push image \"{image}\" to its registry (publishes it outside this machine)", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _RunDockerCliAsync(["push", image], cancellationToken);
    }

    // ---- Volumes ----------------------------------------------------------------------------------------------

    [McpServerTool(Name = "remove_volume")]
    [Description("Removes a volume (docker volume rm). Set force=true to remove one still referenced. This deletes the volume's data, so it asks the operator afresh each time with the volume name shown, and is never remembered.")]
    public Task<string> RemoveVolume(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The volume name.")] string volume,
        [Description("Force removal even if still referenced. Default false.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        _MutateAsync($"remove volume \"{volume}\" (force={force}) — this deletes its data", session,
            token => engine.RemoveVolumeAsync(volume, force, token),
            new { ok = true, removed = volume }, cancellationToken);

    [McpServerTool(Name = "prune")]
    [Description("Reclaims disk by pruning unused resources (docker prune). target is one of \"containers\" (stopped containers), \"images\" (dangling images), or \"volumes\" (volumes no container uses). This deletes things, so it asks the operator afresh each time with the target shown, and is never remembered. Returns the bytes reclaimed and what was removed.")]
    public async Task<string> Prune(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("What to prune: \"containers\", \"images\" or \"volumes\".")] string target,
        CancellationToken cancellationToken = default)
    {
        if (!_TryParsePruneTarget(target, out var pruneTarget))
        {
            return McpText.Error("target must be one of \"containers\", \"images\" or \"volumes\".");
        }

        var decision = await gate.AuthorizeMutationAsync($"prune {pruneTarget.ToString().ToLowerInvariant()} — this permanently removes the unused ones", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var result = await engine.PruneAsync(pruneTarget, cancellationToken);
            return McpText.Ok(new { ok = true, spaceReclaimedBytes = result.SpaceReclaimedBytes, deleted = result.Deleted });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    // ---- Build / cp (arbitrary code / container-fs writes — behind the exec capability) ------------------------

    [McpServerTool(Name = "build_image")]
    [Description("Builds an image from a Dockerfile (docker build -t <tag> <context>). Because a build runs the Dockerfile's RUN steps — arbitrary code — this is off unless the exec capability is on, and always asks afresh with the literal command shown. Never remembered. Runs through the docker CLI.")]
    public async Task<string> BuildImage(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The build context directory (holds the Dockerfile).")] string context,
        [Description("The tag to give the built image, e.g. \"myapp:latest\".")] string tag,
        [Description("Optional Dockerfile path relative to the context. Default: Dockerfile.")] string? dockerfile = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "build", "-t", tag };
        if (!string.IsNullOrWhiteSpace(dockerfile))
        {
            args.Add("-f");
            args.Add(dockerfile!);
        }

        args.Add(context);

        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Exec, settings.AllowExec, $"docker {string.Join(' ', args)}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _RunDockerCliAsync(args, cancellationToken);
    }

    [McpServerTool(Name = "cp")]
    [Description("Copies files between the host and a container (docker cp). One of source/dest is \"container:/path\" and the other a host path. Because it reads or writes the container's filesystem, this is off unless the exec capability is on, and always asks afresh with the literal command shown. Never remembered. Runs through the docker CLI.")]
    public async Task<string> Cp(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("Source: a host path, or \"container:/path/in/container\".")] string source,
        [Description("Destination: a host path, or \"container:/path/in/container\".")] string destination,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Exec, settings.AllowExec, $"docker cp {source} {destination}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _RunDockerCliAsync(["cp", source, destination], cancellationToken);
    }

    // ---- Compose (docker compose CLI) --------------------------------------------------------------------------

    [McpServerTool(Name = "compose_config")]
    [Description("Resolves and returns the fully-rendered Compose configuration for a project (docker compose config) — the safe way to see exactly what an 'up' would create before you run it. A read: needs only the one-time daemon-connection consent.")]
    public async Task<string> ComposeConfig(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory. Default: docker-compose.yml auto-detected.")] string? file = null,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"docker compose config (in {directory})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _RunComposeAsync(directory, _ComposeArgs(file, "config"), cancellationToken);
    }

    [McpServerTool(Name = "compose_logs")]
    [Description("Returns the recent logs of a Compose project's services (docker compose logs), optionally filtered to specific services. Set tail to how many lines from the end (default 200; 0 means all). Does not follow — it returns what is there now and completes. A read: needs only the one-time daemon-connection consent.")]
    public async Task<string> ComposeLogs(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory.")] string? file = null,
        [Description("Optional specific services to show logs for; empty means all.")] string[]? services = null,
        [Description("How many lines from the end to return. Default 200; 0 means all.")] int tail = 200,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"docker compose logs (in {directory})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        var args = _ComposeArgs(file, "logs", "--no-color", "--no-log-prefix", "--tail", tail <= 0 ? "all" : tail.ToString());
        _AppendServices(args, services);
        return await _RunComposeAsync(directory, args, cancellationToken);
    }

    [McpServerTool(Name = "compose_up")]
    [Description("Brings a Compose project up in the background (docker compose up -d). A change, so it asks the operator afresh with the literal command shown, and is never remembered. Use compose_config first to see what will be created.")]
    public Task<string> ComposeUp(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory.")] string? file = null,
        [Description("Optional specific services to bring up; empty means all.")] string[]? services = null,
        CancellationToken cancellationToken = default)
    {
        var args = _ComposeArgs(file, "up", "-d");
        _AppendServices(args, services);
        return _ComposeMutateAsync(directory, args, session, cancellationToken);
    }

    [McpServerTool(Name = "compose_down")]
    [Description("Stops and removes a Compose project's containers, networks and default volumes (docker compose down). A change, so it asks the operator afresh with the literal command shown, and is never remembered.")]
    public Task<string> ComposeDown(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory.")] string? file = null,
        CancellationToken cancellationToken = default) =>
        _ComposeMutateAsync(directory, _ComposeArgs(file, "down"), session, cancellationToken);

    [McpServerTool(Name = "compose_build")]
    [Description("Builds (or rebuilds) a Compose project's service images (docker compose build). A change, so it asks the operator afresh with the literal command shown, and is never remembered.")]
    public Task<string> ComposeBuild(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory.")] string? file = null,
        [Description("Optional specific services to build; empty means all.")] string[]? services = null,
        CancellationToken cancellationToken = default)
    {
        var args = _ComposeArgs(file, "build");
        _AppendServices(args, services);
        return _ComposeMutateAsync(directory, args, session, cancellationToken);
    }

    [McpServerTool(Name = "compose_ps")]
    [Description("Shows the status of a Compose project's services (docker compose ps): which are up, their state and ports. A read: needs only the one-time daemon-connection consent.")]
    public async Task<string> ComposePs(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory.")] string? file = null,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"docker compose ps (in {directory})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _RunComposeAsync(directory, _ComposeArgs(file, "ps"), cancellationToken);
    }

    [McpServerTool(Name = "compose_restart")]
    [Description("Restarts a Compose project's services, or specific ones (docker compose restart) — without recreating them, so it is quicker than down+up. A change, so it asks the operator afresh with the literal command shown, and is never remembered.")]
    public Task<string> ComposeRestart(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The project directory that holds the compose file.")] string directory,
        [Description("Optional compose file name/path, relative to the directory.")] string? file = null,
        [Description("Optional specific services to restart; empty means all.")] string[]? services = null,
        CancellationToken cancellationToken = default)
    {
        var args = _ComposeArgs(file, "restart");
        _AppendServices(args, services);
        return _ComposeMutateAsync(directory, args, session, cancellationToken);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private async Task<string> _RunDockerCliAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var result = await docker.RunAsync(args, cancellationToken);
            return McpText.Ok(new
            {
                ok = result.ExitCode == 0,
                exitCode = result.ExitCode,
                stdout = result.Stdout,
                stderr = result.Stderr,
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error($"The docker command could not be run ({ex.GetType().Name}). Check that the docker CLI is installed and on PATH.");
        }
    }

    private static bool _TryParsePruneTarget(string target, out PruneTarget result)
    {
        switch (target.Trim().ToLowerInvariant())
        {
            case "containers": result = PruneTarget.Containers; return true;
            case "images": result = PruneTarget.Images; return true;
            case "volumes": result = PruneTarget.Volumes; return true;
            default: result = default; return false;
        }
    }

    private async Task<string> _MutateAsync(string operation, string session, Func<CancellationToken, Task> action, object success, CancellationToken cancellationToken)
    {
        var decision = await gate.AuthorizeMutationAsync(operation, session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            await action(cancellationToken);
            return McpText.Ok(success);
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
    }

    private async Task<string> _ComposeMutateAsync(string directory, List<string> args, string session, CancellationToken cancellationToken)
    {
        var decision = await gate.AuthorizeMutationAsync($"docker compose {string.Join(' ', args)} (in {directory})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _RunComposeAsync(directory, args, cancellationToken);
    }

    private async Task<string> _RunComposeAsync(string directory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var result = await compose.RunAsync(directory, args, cancellationToken);
            return McpText.Ok(new
            {
                ok = result.ExitCode == 0,
                exitCode = result.ExitCode,
                stdout = result.Stdout,
                stderr = result.Stderr,
            });
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            return McpText.Error($"The docker compose command could not be run ({ex.GetType().Name}). Check that Docker Compose is installed and the directory and compose file are correct.");
        }
    }

    // Service names are agent-supplied; a value starting with '-' would otherwise be parsed by `docker compose` as an
    // option (argument injection). The `--` terminator forces everything after it to be treated as service names.
    private static void _AppendServices(List<string> args, string[]? services)
    {
        if (services is { Length: > 0 })
        {
            args.Add("--");
            args.AddRange(services);
        }
    }

    private static List<string> _ComposeArgs(string? file, string subcommand, params string[] tail)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(file))
        {
            args.Add("-f");
            args.Add(file!);
        }

        args.Add(subcommand);
        args.AddRange(tail);
        return args;
    }

    /// <summary>Reconstruct a verbatim "docker run -d ..." command line for the consent prompt, so dangerous flags
    /// (--privileged, -v binds) are shown literally to the operator.</summary>
    private static string _RunCommandLine(RunSpec spec)
    {
        var parts = new List<string> { "docker", "run", "-d" };
        if (!string.IsNullOrWhiteSpace(spec.Name))
        {
            parts.Add("--name");
            parts.Add(spec.Name!);
        }

        if (spec.Privileged)
        {
            parts.Add("--privileged");
        }

        foreach (var value in spec.Env ?? [])
        {
            parts.Add("-e");
            parts.Add(value);
        }

        foreach (var value in spec.Publish ?? [])
        {
            parts.Add("-p");
            parts.Add(value);
        }

        foreach (var value in spec.Binds ?? [])
        {
            parts.Add("-v");
            parts.Add(value);
        }

        parts.Add(spec.Image);
        parts.AddRange(spec.Command ?? []);
        return string.Join(' ', parts);
    }

    /// <summary>Turn a daemon error into a short, safe message — never leak the raw endpoint or a stack trace to the agent.</summary>
    private static string _Sanitize(Exception ex) =>
        $"The Docker daemon could not be reached or the request failed ({ex.GetType().Name}). Check that the daemon is running and the endpoint in the plugin settings is correct.";
}
