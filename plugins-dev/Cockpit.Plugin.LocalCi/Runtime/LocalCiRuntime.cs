namespace Cockpit.Plugin.LocalCi.Runtime;

// Probes Docker and act, and remembers the answer until something invalidates it.
// Both probes run with a deadline. On Windows the Docker CLI talks to the engine over a named pipe, and a pipe
// whose engine has gone away does not answer "no" — it does not answer. A probe without a deadline would therefore
// hang the caller, which is the settings dialog.
internal sealed class LocalCiRuntime(ICliRunner runner) : ILocalCiRuntime, IDisposable
{
    // Generous enough for a cold Docker Desktop, short enough that a dead pipe is not a hang.
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Volatile because the fast path reads it outside the gate: without it the memory model permits a reader to keep
    // seeing an answer Invalidate has already dropped.
    private volatile LocalCiRuntimeStatus? _cached;

    public async Task<LocalCiRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } hit)
        {
            return hit;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cached ??= new LocalCiRuntimeStatus(
                await _DetectDockerAsync(cancellationToken),
                await _DetectActAsync(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    public void Dispose() => _gate.Dispose();

    // One call answers both questions. `docker version` reaches through to the server, so a zero exit proves
    // the engine answered, and the same format string brings back which kind of containers it runs — asking twice
    // would only add a second way to hang.
    private async Task<DockerRuntimeStatus> _DetectDockerAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "docker",
            ["version", "--format", "{{.Server.Os}} {{.Server.Version}}"],
            ProbeTimeout,
            cancellationToken);

        if (!result.Started)
        {
            return DockerRuntimeStatus.NotInstalled;
        }

        if (!result.Succeeded)
        {
            return DockerRuntimeStatus.EngineNotRunning;
        }

        var (containerOs, serverVersion) = _SplitServerLine(result.StandardOutput);
        return new DockerRuntimeStatus(DockerRuntimeState.Usable, containerOs, serverVersion);
    }

    private async Task<ActRuntimeStatus> _DetectActAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("act", ["--version"], ProbeTimeout, cancellationToken);
        if (!result.Succeeded)
        {
            return ActRuntimeStatus.NotInstalled;
        }

        return new ActRuntimeStatus(IsInstalled: true, _ReadActVersion(result.StandardOutput));
    }

    // "linux 29.5.3" — either half may be empty if the engine reported it as such.
    private static (string? ContainerOs, string? ServerVersion) _SplitServerLine(string output)
    {
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1));
    }

    // act prints "act version 0.2.89"; older builds print the bare number.
    private static string? _ReadActVersion(string output)
    {
        var line = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }
}
