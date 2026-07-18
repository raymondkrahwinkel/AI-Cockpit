using Cockpit.Plugin.Docker.Compose;

namespace Cockpit.Plugin.Docker.Tests;

/// <summary>A fake <see cref="IComposeCli"/> that records invocations instead of spawning a process.</summary>
internal sealed class FakeComposeCli : IComposeCli
{
    public List<(string Directory, IReadOnlyList<string> Args)> Calls { get; } = [];

    public ComposeResult Result { get; set; } = new(0, "done", string.Empty);

    public Task<ComposeResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Calls.Add((workingDirectory, args));
        return Task.FromResult(Result);
    }
}
