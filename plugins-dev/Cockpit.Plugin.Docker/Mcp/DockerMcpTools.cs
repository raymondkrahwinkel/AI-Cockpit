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
        catch (Exception ex)
        {
            return McpText.Error(_Sanitize(ex));
        }
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

    // ---- Helpers -----------------------------------------------------------------------------------------------

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
