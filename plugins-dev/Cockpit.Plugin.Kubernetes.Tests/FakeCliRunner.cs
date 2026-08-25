using Cockpit.Plugin.Kubernetes.Cli;

namespace Cockpit.Plugin.Kubernetes.Tests;

// An in-memory ICliRunner for tests that must not depend on a real `kind` binary. Every call is recorded, and the
// response is scripted per test — default is a plain success, matching what a real create/delete run would return.
internal sealed class FakeCliRunner : ICliRunner
{
    public List<CliCommand> Calls { get; } = [];

    public Func<CliCommand, CliResult> Handler { get; set; } = _ => CliResult.Exited(0, string.Empty, string.Empty);

    public Task<CliResult> RunAsync(CliCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add(command);
        return Task.FromResult(Handler(command));
    }
}
