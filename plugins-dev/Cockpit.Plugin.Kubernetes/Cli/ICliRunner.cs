namespace Cockpit.Plugin.Kubernetes.Cli;

// An interface purely so a fake can stand in for tests that must not depend on an external binary being on PATH —
// same role IHelmRunner already plays for HelmRunner, extended here so KindClusterManager gets it too.
internal interface ICliRunner
{
    Task<CliResult> RunAsync(CliCommand command, TimeSpan timeout, CancellationToken cancellationToken = default);
}
