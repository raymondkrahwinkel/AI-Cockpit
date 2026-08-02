using Cockpit.Plugin.Docker.Compose;

namespace Cockpit.Plugin.Docker.Tests;

// A fake `IDockerCli` that records invocations instead of spawning a process.
internal sealed class FakeDockerCli : IDockerCli
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public DockerCliResult Result { get; set; } = new(0, "done", string.Empty);

    public Task<DockerCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Calls.Add(args);
        return Task.FromResult(Result);
    }
}
