using Cockpit.Plugin.Kubernetes.Cli;

namespace Cockpit.Plugin.Kubernetes.Helm;

// `IHelmRunner` backed by the generic `CliRunner` (AC-179 pulled the process-handling out so kind could reuse it).
// Only translates between helm's `HelmCommand`/`HelmResult` shapes and `CliRunner`'s generic ones — every existing
// caller and test keeps working against the same `IHelmRunner` contract as before.
internal sealed class HelmRunner : IHelmRunner
{
    private readonly CliRunner _cli = new();

    public async Task<HelmResult> RunAsync(HelmCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Values reach helm on stdin (`-f -`), never through a file: a values document an agent composed can carry
        // secrets, and writing it to disk is the very thing this plugin promises not to do. `CliRunner` already
        // honors `StandardInput` the same way, so that guarantee travels through the translation unchanged.
        var cliCommand = new CliCommand(command.FileName, command.Arguments, command.Environment, command.StandardInput);
        var result = await _cli.RunAsync(cliCommand, timeout, cancellationToken);
        return new HelmResult(result.Started, result.TimedOut, result.ExitCode, result.Stdout, result.Stderr);
    }
}
