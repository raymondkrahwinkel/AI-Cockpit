namespace Cockpit.Plugin.Kubernetes.Cli;

// An interface purely so a fake can stand in for tests that must not depend on an external binary being on PATH —
// the same role IHelmRunner already plays for HelmRunner.
internal interface ICliRunner
{
    Task<CliResult> RunAsync(CliCommand command, TimeSpan timeout, CancellationToken cancellationToken = default);
}
